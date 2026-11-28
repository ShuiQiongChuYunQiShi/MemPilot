using System.Net.Http;
using System.Text.Json;
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
    /// 生效的排除集（v0.23.0）= 内置默认（挂起即断网：代理/VPN 内核）+ 调用方 denyNames。
    /// 默认名单总是生效——它防的是"挂起后连 agent 自己的 API 调用都断了"这类事故。
    /// </summary>
    private static HashSet<string> DenySet(LeaseRequest req)
        => LeaseHardPlanner.MergeDenyNames(req.DenyNames);

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
            req.TargetAvailableGb, policy, currentGb, age, excluded, req.DenyNames,
                // 前两次枚举的 CPU/IO 差值没有基线，会虚高成"忙" → 候选集为空
                samplingWarm: _enumerationCount >= 2);

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

    // ---- 为 AI 任务自动预留（v0.25.0，默认关）----

    private string? _autoLeaseId;

    /// <summary>
    /// AI 会话开始 → 申请软租约；结束 → 归还。
    ///
    /// 为什么只做 **soft**：hard 会挂起进程，而"自动挂起"离本项目"只观察不处置"的原则太远。
    /// soft 只清备用列表 + 压缩空闲后台，完全可逆、不改任何进程状态。
    ///
    /// 为什么 TTL 12 小时且不续期：实测最长 AI 会话 212 分钟，12h 足够；自动续期只会
    /// 多一条要维护的路径（租约有 RenewCount 可查，真不够再加）。
    ///
    /// 为什么冲突就跳过：手动申请是用户的明确意图，自动预留是可再生的，不该抢。
    /// 反过来，手动申请会挤掉自动预留（见 LeaseApiService 的让位逻辑）。
    ///
    /// 固有短板：AI 会话检测有 2–2.5 分钟延迟（5 个 30 秒样本里 ≥3 个），
    /// 所以只对长任务有效——短脚本用 `mempilot lease run`。
    /// </summary>
    private void HandleAutoLeaseForAi(bool started)
    {
        var cfg = ConfigStore.Config;
        if (!cfg.AutoLeaseForAiEnabled || !cfg.LeaseApiEnabled)
        {
            if (!started) ReleaseAutoLease("自动预留已关闭");
            return;
        }

        if (!started)
        {
            ReleaseAutoLease("AI 会话结束");
            return;
        }
        if (_autoLeaseId != null) return;          // 已有自动租约，不重复申请

        double gb = Math.Clamp(cfg.AutoLeaseForAiGb, 1, 64);
        _ = Task.Run(async () =>
        {
            try
            {
                var http = new LeaseHttp();
                if (!http.HasToken) return;
                var body = JsonSerializer.Serialize(new
                {
                    targetAvailableGb = gb,
                    ttlSeconds = 12 * 3600,
                    policy = "soft",
                    requester = "auto:ai-session"
                });
                var r = await http.CallAsync(HttpMethod.Post, "/api/lease", body).ConfigureAwait(false);
                if (r.Ok && ReadLeaseId(r.Body) is { Length: > 0 } id)
                {
                    SetUi(() =>
                    {
                        _autoLeaseId = id;
                        Decisions.Log(new DecisionEntry
                        {
                            Kind = "Lease", Target = "AI 自动预留", Decision = "开始",
                            Code = "lease.auto_acquired",
                            Args = new[] { gb.ToString("0.#"), id },
                            Reason = $"检测到 AI 任务，自动预留 {gb:0.#} GB（soft，12h）；租约 {id}"
                        });
                    });
                }
                else if (r.Status == 409)
                {
                    SetUi(() => Decisions.Log(new DecisionEntry
                    {
                        Kind = "Lease", Target = "AI 自动预留", Decision = "跳过",
                        Code = "lease.auto_skipped",
                        Reason = "已有租约在占用，自动预留跳过（手动优先）"
                    }));
                }
                else
                {
                    SetUi(() => Decisions.Log(new DecisionEntry
                    {
                        Kind = "Lease", Target = "AI 自动预留", Decision = "失败",
                        Code = "lease.auto_failed",
                        Args = new[] { r.Status.ToString() },
                        Reason = $"自动预留失败：{http.Explain(r)}"
                    }));
                }
            }
            catch (Exception ex) { Diag.Record("lease:auto-acquire", ex); }
        });
    }

    private void ReleaseAutoLease(string reason)
    {
        var id = _autoLeaseId;
        if (id == null) return;
        _autoLeaseId = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var http = new LeaseHttp();
                var r = await http.CallAsync(HttpMethod.Delete, "/api/lease/" + id).ConfigureAwait(false);
                // 404 = 已经不在了（比如被手动申请挤掉），不是错误
                if (!r.Ok && r.Status != 404)
                    Diag.Record("lease:auto-release", new Exception($"HTTP {r.Status}: {r.Body}"));
                SetUi(() => Decisions.Log(new DecisionEntry
                {
                    Kind = "Lease", Target = "AI 自动预留", Decision = "归还",
                    Code = "lease.auto_released",
                    Args = new[] { reason },
                    Reason = $"自动预留已归还（{reason}）"
                }));
            }
            catch (Exception ex) { Diag.Record("lease:auto-release", ex); }
        });
    }

    private static string? ReadLeaseId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var el) ? el.GetString() : null;
        }
        catch { return null; }
    }

    private (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended) ExecuteLeaseRelief(
        LeaseRequest req, LeasePolicy policy)
    {
        // v0.24.1：腾挪一开始就把申请方告诉引擎。reserving 阶段（占位后、完成前）也可能撞上
        // FloorTrim，而 FloorTrim 是独立路径、不读租约账本。失败/取消时这个值可能残留，
        // 后果是"多排除一个 PID"——安全方向，无害。
        Engine.LeaseRequesterPid = req.RequesterPid;

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
        var deny = DenySet(req);

        // 二级：安全子集压缩
        var safe = TrimSelection.BuildSafeTrimSet(_lastProcs, fg, audio, excluded, Cleanup.TrimFeedback.RatioOf);
        int safeBefore = safe.Count;
        safe.RemoveAll(p => !string.IsNullOrEmpty(p.Name) && deny.Contains(p.Name));
        int deniedCount = safeBefore - safe.Count;

        var trim = Cleanup.TrimWorkingSets(safe);
        var detail = UiStrings.F("Lease_TrimDetail", purgeNote, trim.Trimmed, _lastProcs.Count - safe.Count);
        if (excluded != null)
            detail += UiStrings.F("Lease_RequesterExcluded", excludedHits);
        if (deniedCount > 0)
            detail += UiStrings.F("Lease_DeniedDetail", deniedCount);

        var suspended = new List<LeaseSuspendedProcess>();
        if (policy == LeasePolicy.Hard)
        {
            // 三级：还不够目标额度就挂起安全子集里"足够闲"的进程（可恢复，归还时逐个恢复）
            double availGb = ProcessService.GetSystemMemory().AvailablePhysicalBytes / 1073741824.0;
            double needGb = req.TargetAvailableGb - availGb;
            if (needGb > 0)
            {
                long needBytes = (long)(needGb * 1073741824.0);
                var candidates = LeaseHardPlanner.PickSuspendCandidates(_lastProcs, fg, audio, DateTime.Now, excluded, deny);
                var picked = LeaseHardPlanner.TakeForShortfall(candidates, needBytes);
                int ok = 0;
                foreach (var p in picked)
                {
                    try
                    {
                        Cleanup.Execute(p, CleanupActionLevel.L2Suspend, 0, suspendReason: "租约挂起");
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

    /// <summary>
    /// 启动 30 秒后恢复"租约孤儿"（v0.24.0）。
    ///
    /// 为什么需要：租约账本（<see cref="LeaseManager"/> 的当前租约）只在内存，而挂起记录
    /// 落盘在 suspended.json。MemPilot 在租约期间崩溃或重启时账本丢了、RestoreHook 也没了，
    /// 那批进程被标成"租约挂起"却永远没人恢复——用户只能自己发现再手动恢复。
    ///
    /// 为什么延迟 30 秒：崩溃后原任务可能还在跑（它也吃内存），立刻唤醒 8 个进程会去抢。
    /// 30 秒够系统稳定，又不至于让用户盯着"已挂起"页困惑太久。
    /// </summary>
    private void ScheduleOrphanLeaseResume()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                var (restored, failed) = Cleanup.ResumeOrphans(CleanupEngine.LeaseSuspendReason);
                if (restored + failed == 0) return;      // 没有孤儿，安静退出
                Decisions.Log(new DecisionEntry
                {
                    Kind = "Lease",
                    Target = "孤儿恢复",
                    Decision = "恢复",
                    Code = "lease.orphan_resume",
                    Args = new[] { restored.ToString(), failed.ToString() },
                    Reason = $"上次运行在租约期间结束、账本已丢失：恢复 {restored} 个挂起进程，失败 {failed} 个"
                });
                SetUi(() => SetActionStatus(UiStrings.F("Lease_OrphanRestored", restored, failed)));
            }
            catch (Exception ex) { Diag.Record("lease:orphan-resume", ex); }
        });
    }
}
