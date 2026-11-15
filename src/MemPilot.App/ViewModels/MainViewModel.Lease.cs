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
                LeaseApiStatus = $"租约 API 监听中：127.0.0.1:{LeaseApi.BoundPort}";
            else
                LeaseApiStatus = $"租约 API 未启动：{LeaseApi.LastError}";
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

    private (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended) ExecuteLeaseRelief(
        LeaseRequest req, LeasePolicy policy)
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
            purgeNote = ok ? UiStrings.Get("Lease_Purged") : $"备用列表清理失败（{pr.message ?? "未知"}）";
        }

        // 二级：安全子集压缩
        var safe = TrimSelection.BuildSafeTrimSet(_lastProcs, fg, audio);
        var trim = Cleanup.TrimWorkingSets(safe);
        var detail = $"{purgeNote}；压缩安全子集 {trim.Trimmed} 个（排除 {_lastProcs.Count - safe.Count} 个）";

        var suspended = new List<LeaseSuspendedProcess>();
        if (policy == LeasePolicy.Hard)
        {
            // 三级：还不够目标额度就挂起安全子集里"足够闲"的进程（可恢复，归还时逐个恢复）
            double availGb = ProcessService.GetSystemMemory().AvailablePhysicalBytes / 1073741824.0;
            double needGb = req.TargetAvailableGb - availGb;
            if (needGb > 0)
            {
                long needBytes = (long)(needGb * 1073741824.0);
                var candidates = LeaseHardPlanner.PickSuspendCandidates(_lastProcs, fg, audio, DateTime.Now);
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
                detail += $"；挂起 {ok} 个闲置进程（可恢复）";
            }
        }

        var after = ProcessService.GetSystemMemory();
        detail = $"{detail}；可用 {before.AvailablePhysicalBytes / 1073741824.0:F1} → " +
                 $"{after.AvailablePhysicalBytes / 1073741824.0:F1} GB";
        return (new LeaseReliefResult(
            before.AvailablePhysicalBytes / 1073741824.0,
            after.AvailablePhysicalBytes / 1073741824.0,
            detail), suspended);
    }
}
