using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

public partial class MainViewModel
{
    private readonly NativeMemoryCleanerService _memCleanerForLease = new();

    /// <summary>重新生成租约令牌（设置页按钮）。</summary>
    public void RegenerateLeaseToken()
    {
        _leaseToken = LeaseTokenStore.Regenerate(ConfigStore.DataDirectory);
        OnPropertyChanged(nameof(LeaseToken));
    }

    /// <summary>租约 API 开关变更/端口变更后重启监听（设置保存时调用）。绝不抛异常。</summary>
    public void RestartLeaseApi()
    {
        try
        {
            LeaseApi.Stop();
            if (!ConfigStore.Config.LeaseApiEnabled)
            {
                LeaseApiStatus = UiStrings.Get("Lease_StatusOff");
                return;
            }
            if (LeaseApi.Start())
                LeaseApiStatus = UiStrings.F("Lease_StatusListening", LeaseApi.BoundPort);
            else
                LeaseApiStatus = UiStrings.F("Lease_StatusNotStartedBody", LeaseApi.LastError);
        }
        catch (Exception ex)
        {
            // 兜底：这个方法会被 async void Loaded 与设置保存调用，任何异常都不能外泄。
            LeaseApiStatus = UiStrings.Get("Lease_StatusNotStarted") + ex.Message;
            try { Diag.Record("lease:start", ex); } catch { }
        }
    }

    private string _leaseApiStatus = UiStrings.Get("Lease_StatusDisabled");
    /// <summary>租约 API 的运行状态（设置页显示；失败时是红字原因，不是静默）。</summary>
    public string LeaseApiStatus
    {
        get => _leaseApiStatus;
        private set { _leaseApiStatus = value; OnPropertyChanged(); }
    }

    private readonly Dictionary<string, DateTime> _planLogTimes = new();

    /// <summary>
    /// 零动作预演（v0.23.0，POST /api/plan）。只读 _lastProcs + 实时内存、只跑纯函数，
    /// **绝不调用 Cleanup/ProcessService 的处置原语**——plan 一旦能动作就不叫预演了。
    /// </summary>
    private LeasePlan BuildLeasePlan(LeaseRequest req, LeasePolicy policy)
    {
        var mem = ProcessService.GetSystemMemory();
        double currentGb = mem.AvailablePhysicalBytes / 1073741824.0;
        var procs = _lastProcs;                        // 不拷贝：plan 只读
        var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
        var excluded = req.RequesterPid > 0
            ? ProcessTreeBuilder.BuildSubtreePids(procs, req.RequesterPid)
            : null;
        double age = _lastProcsUtc == DateTime.MinValue
            ? -1
            : (DateTime.UtcNow - _lastProcsUtc).TotalSeconds;

        var plan = LeasePlanner.Build(procs, Engine.LastForegroundPid, audio, DateTime.Now,
            req.TargetAvailableGb, policy, currentGb, age, excluded);

        LogLeasePlanOnce(req, plan);
        return plan;
    }

    /// <summary>
    /// 同一 (requester, 目标, 策略) 30 秒内只记一条。agent 可能每次工具调用前都 plan 一次，
    /// 不去重会把决策日志刷成 plan 流水账——plan 是预演不是处置，不该挤掉真实处置记录。
    /// 注意 "LeasePlan" **不在** DecisionLog.ImportantKinds 里：它有总量上限、但没有配额保护，
    /// 这样 800 条重要配额仍然只留给"真的发生了什么"。
    /// </summary>
    private void LogLeasePlanOnce(LeaseRequest req, LeasePlan plan)
    {
        var key = $"{req.Requester}|{req.TargetAvailableGb:F1}|{plan.Policy}";
        lock (_planLogTimes)
        {
            var nowUtc = DateTime.UtcNow;
            if (_planLogTimes.TryGetValue(key, out var last) && (nowUtc - last).TotalSeconds < 30) return;
            if (_planLogTimes.Count > 64) _planLogTimes.Clear();
            _planLogTimes[key] = nowUtc;
        }
        Decisions.Log(new DecisionEntry
        {
            Kind = "LeasePlan",
            Target = req.Requester ?? "",
            Decision = plan.Feasible ? "可行" : "缺口",
            Reason = $"预演 {req.TargetAvailableGb:F1} GB（{plan.Policy}）：当前 {plan.CurrentAvailableGb:F1}，" +
                     $"预计 {plan.ProjectedAvailableGb:F1}（{plan.ProjectionBasis}），" +
                     $"将挂起 {plan.WouldSuspend.Count} 个；安全边界排除 {plan.Excluded.Total} 个"
        });
    }

    private (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended) ExecuteLeaseRelief(        LeaseRequest req, LeasePolicy policy)
    {
        var before = ProcessService.GetSystemMemory();
        var audio = new HashSet<int>(_lastProcs.Where(p => p.AudioActive).Select(p => p.Pid));
        var fg = Engine.LastForegroundPid;

        // 一级：清低优先级备用列表（零风险）。
        // v0.10.0：检查权限与返回值——非提权时 NtSetSystemInformation 会失败，
        // 若不检查就会在租约响应里宣称"已清备用列表"而实际什么也没做。
        string purgeNote;
        if (!_memCleanerForLease.IsAdmin())
        {
            purgeNote = UiStrings.Get("Lease_SkipPurgeNoAdmin");
        }
        else
        {
            var purge = _memCleanerForLease.CleanAreas(new List<MemoryArea> { MemoryArea.StandbyListLowPriority });
            bool ok = purge.TryGetValue(MemoryArea.StandbyListLowPriority, out var pr) && pr.success;
            purgeNote = ok ? UiStrings.Get("Lease_Purged")
                : UiStrings.F("Lease_PurgeFailed", pr.message ?? UiStrings.Get("Common_Unknown"));
        }

        // v0.23.0 硬不变式：调用方自身及其子树永不处置。
        // agent 的 worker 往往"无窗口 + 空闲"，恰好是硬租约的候选画像——
        // 不排除就会把自己挂起，然后一直等一个永远不会返回的任务。
        var excluded = req.RequesterPid > 0
            ? ProcessTreeBuilder.BuildSubtreePids(_lastProcs, req.RequesterPid)
            : null;
        int excludedHits = excluded == null ? 0 : _lastProcs.Count(p => excluded.Contains(p.Pid));

        // 二级：安全子集压缩
        var safe = TrimSelection.BuildSafeTrimSet(_lastProcs, fg, audio, excluded);
        var trim = Cleanup.TrimWorkingSets(safe);
        var detail = UiStrings.F("Lease_TrimDetail", purgeNote, trim.Trimmed, _lastProcs.Count - safe.Count);
        if (excluded != null)
            detail += UiStrings.F("Lease_RequesterExcluded", excludedHits);

        var suspended = new List<LeaseSuspendedProcess>();
        if (policy == LeasePolicy.Hard)
        {
            // 三级：还不够目标额度就挂起安全子集里"足够闲"的进程（可恢复，归还时逐个恢复）
            double availGb = ProcessService.GetSystemMemory().AvailablePhysicalBytes / 1073741824.0;
            double needGb = req.TargetAvailableGb - availGb;
            if (needGb > 0)
            {
                long needBytes = (long)(needGb * 1073741824.0);
                var candidates = LeaseHardPlanner.PickSuspendCandidates(_lastProcs, fg, audio, DateTime.Now, excluded);
                var picked = LeaseHardPlanner.TakeForShortfall(candidates, needBytes);
                int ok = 0;
                foreach (var p in picked)
                {
                    try
                    {
                        Cleanup.Execute(p, CleanupActionLevel.L2Suspend, 0);
                        suspended.Add(new LeaseSuspendedProcess
                        {
                            Pid = p.Pid, StartTime = p.StartTime, Name = p.Name,
                            PrivateBytes = p.Memory.PrivateBytes
                        });
                        ok++;
                    }
                    catch (Exception ex) { Diag.Record("lease:suspend", ex); }
                }
                detail += UiStrings.F("Lease_SuspendDetail", ok);
            }
        }

        var after = ProcessService.GetSystemMemory();
        detail = UiStrings.F("Lease_AvailableDetail", detail,
            before.AvailablePhysicalBytes / 1073741824.0,
            after.AvailablePhysicalBytes / 1073741824.0);
        return (new LeaseReliefResult(
            before.AvailablePhysicalBytes / 1073741824.0,
            after.AvailablePhysicalBytes / 1073741824.0,
            detail), suspended);
    }
}
