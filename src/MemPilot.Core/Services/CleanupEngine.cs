using System.Diagnostics;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// Executes cleanup actions (the L0-L4 ladder) and builds/executes make-room plans.
/// Action semantics:
///   L1 trim: pushes private working set to standby — cheap, safe, best-effort.
///   L2 suspend: freezes CPU activity only; does NOT free memory. Tracked in SuspendRegistry.
///   L3 graceful close: WM_CLOSE to owned windows, waits for exit, optional L4 escalation.
///   L4 force kill: last resort, never applied automatically by the resident engine.
/// </summary>
public class CleanupEngine
{
    private readonly ProcessService _procs;
    private readonly AuditLogger _audit;
    private readonly NativeMemoryCleanerService _memCleaner = new();
    public SuspendRegistry Suspended { get; }
    public DisposalRegistry Disposals { get; }

    public CleanupEngine(ProcessService procs, AuditLogger audit, string dataDir)
    {
        _procs = procs;
        _audit = audit;
        Suspended = new SuspendRegistry(dataDir);
        Disposals = new DisposalRegistry(dataDir);
    }

    public record ActionResult(bool Success, string Message, bool Exited);

    public record BatchTrimResult(int Trimmed, int Failed, long FreedBytes);

    /// <summary>
    /// 压缩给定候选集的工作集（EmptyWorkingSet）。只压缩、不结束任何进程。
    ///
    /// v0.9.2（三轮复盘 P2）：**不再内部筛选**——原来这里用的是全套 6 份安全判定里最弱的一份
    /// （只看受保护/系统/安全/反作弊/驱动 + CPU），不看用途、音频、白名单、最小存活期。
    /// 现在要求调用方传入已筛选的候选集（`TrimSelection.BuildSafeTrimSet`），
    /// 保证"什么问题都不看"的路径不存在；自身 PID 仍然无条件排除。
    /// </summary>
    public BatchTrimResult TrimWorkingSets(List<ProcessInfo> procs)
    {
        long before = _procs.GetSystemMemory().AvailablePhysicalBytes;
        int ok = 0, fail = 0, self = Environment.ProcessId;
        foreach (var p in procs)
        {
            if (p.Pid == self) continue;      // 永远不压自己
            try { _procs.TrimWorkingSet(p.Pid); ok++; }
            catch { fail++; }
        }
        long after = _procs.GetSystemMemory().AvailablePhysicalBytes;
        long freed = Math.Max(0, after - before);
        _audit.Log(new AuditEntry
        {
            Action = "L1TrimAll",
            Target = "-",
            Level = "L1Trim",
            Result = $"压缩 {ok} 个进程工作集（失败 {fail}），可用内存 +{freed / 1048576.0:F0} MB",
            MemoryBeforeBytes = before,
            MemoryAfterBytes = after
        });
        return new BatchTrimResult(ok, fail, freed);
    }

    /// <param name="suspendReason">L2 挂起写进账本的原因（v0.24.0）。租约挂起传"租约挂起"，
    /// 这样启动时可以识别"租约期间崩溃留下的孤儿"并自动恢复——旧实现硬编码"手动挂起"，
    /// 而租约账本只在内存，崩溃重启后那批进程没有任何恢复路径。</param>
    public ActionResult Execute(ProcessInfo p, CleanupActionLevel level, int gracefulTimeoutSec,
        bool escalateToKill = false, string suspendReason = "手动挂起")
    {
        // v0.18.0（P0）：复检与动作共用同一句柄，消除 TOCTOU。
        // 旧实现按 PID 复检一次、再按 PID OpenProcess 一次——两次打开之间进程可能退出、
        // PID 被复用，复检通过不代表动作打在同一对象上。
        // L2 及以上必须验证通过才动手；启动时间读不到（MinValue）一律拒绝——挂起登记会把
        // 这个未知值写进 SuspendRegistry，到期恢复必然比对失败，进程会永久挂起。
        // L1 只压缩工作集、不改变进程状态，验证失败时回退按 PID 执行（零风险动作不该被连坐）。
        IntPtr handle = IntPtr.Zero;
        if (level >= CleanupActionLevel.L2Suspend)
        {
            uint access = level switch
            {
                CleanupActionLevel.L2Suspend => NativeMethods.PROCESS_SUSPEND_RESUME,
                CleanupActionLevel.L4ForceKill => NativeMethods.PROCESS_TERMINATE,
                _ => escalateToKill
                    ? NativeMethods.PROCESS_TERMINATE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION
                    : NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION
            };
            handle = _procs.OpenVerified(p.Pid, access, p.StartTime);
            if (handle == IntPtr.Zero)
            {
                var msg = p.StartTime == DateTime.MinValue
                    ? "无法确认进程身份（读不到启动时间），已拒绝执行"
                    : $"目标已不是原进程（PID {p.Pid} 可能已被复用或已退出），已拒绝执行";
                _audit.Log(new AuditEntry
                {
                    Action = level.ToString(),
                    Target = $"{p.Name} (PID {p.Pid})",
                    Level = level.ToString(),
                    Result = "跳过：" + msg
                });
                return new ActionResult(false, msg, false);
            }
        }
        else if (level == CleanupActionLevel.L1Trim)
        {
            handle = _procs.OpenVerified(p.Pid,
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA, p.StartTime);
        }

        long before = _procs.GetSystemMemory().AvailablePhysicalBytes;
        string result;
        bool exited = false;
        try
        {
            switch (level)
            {
                case CleanupActionLevel.L1Trim:
                    {
                        // v0.13.0 遥测修复：记录被压缩进程自己的私有工作集差值（旧的全局
                        // Before/After 在 0.6s 窗口里全是系统噪声，出现"回收后可用反而降"的假账）
                        long wsBefore = handle != IntPtr.Zero
                            ? _procs.ReadPrivateWorkingSetHandle(handle)
                            : _procs.ReadPrivateWorkingSet(p.Pid);
                        if (handle != IntPtr.Zero) _procs.TrimWorkingSetHandle(handle);
                        else _procs.TrimWorkingSet(p.Pid);
                        System.Threading.Thread.Sleep(300);
                        long wsAfter = handle != IntPtr.Zero
                            ? _procs.ReadPrivateWorkingSetHandle(handle)
                            : _procs.ReadPrivateWorkingSet(p.Pid);
                        result = wsBefore > 0 && wsAfter >= 0
                            ? $"工作集已回收（该进程私有工作集 {wsBefore / 1048576.0:F0} → {wsAfter / 1048576.0:F0} MB）"
                            : "工作集已回收";
                    }
                    break;
                case CleanupActionLevel.L2Suspend:
                    _procs.SuspendProcessHandle(handle);
                    // 身份已验证，StartTime 一定有效——到期恢复才能配对成功
                    Suspended.Add(p.Pid, p.StartTime, p.Name, suspendReason);
                    result = "进程已挂起（可在“已挂起”中恢复）";
                    break;
                case CleanupActionLevel.L3GracefulClose:
                    // 句柄所有权转移给 GracefulClose（它在 finally 里关闭）
                    var r = GracefulClose(p, gracefulTimeoutSec, escalateToKill, handle);
                    handle = IntPtr.Zero;
                    result = r.message; exited = r.exited;
                    break;
                case CleanupActionLevel.L4ForceKill:
                    _procs.TerminateProcessHandle(handle);
                    result = "已强制结束";
                    exited = true;
                    break;
                default:
                    result = "级别无效";
                    break;
            }
        }
        catch (Exception ex)
        {
            result = "失败: " + ex.Message;
        }
        finally
        {
            ProcessService.CloseProcessHandle(handle);   // L3 转移所有权时 handle 已置零
        }
        long after = _procs.GetSystemMemory().AvailablePhysicalBytes;
        _audit.Log(new AuditEntry
        {
            Action = level.ToString(),
            Target = $"{p.Name} (PID {p.Pid})",
            Level = level.ToString(),
            Result = result,
            MemoryBeforeBytes = before,
            MemoryAfterBytes = after,
            Details = p.CategoryReason
        });
        WindowsEventLog.TryWrite($"MemPilot {level}: {p.Name} (PID {p.Pid}) -> {result}");
        // keep a relaunchable record of close/kill attempts (the closest thing to an undo)
        if (level is CleanupActionLevel.L3GracefulClose or CleanupActionLevel.L4ForceKill)
        {
            Disposals.Add(new DisposalRecord
            {
                Pid = p.Pid,
                Name = p.Name,
                ExePath = p.ExecutablePath,
                Action = level.ToString(),
                Exited = exited
            });
        }
        return new ActionResult(result.StartsWith("失败") == false, result, exited);
    }

    /// <summary>
    /// 目标是否仍是快照里那个进程：PID 相同且启动时间一致（PID 复用会拿到不同 startTime）。
    /// v0.18.0（P0）：StartTime 未知时由「放行」改为**拒绝**。旧行为会把 DateTime.MinValue
    /// 写进 SuspendRegistry，到期恢复时 IsAliveSame 必然比对失败 → 进程永久挂起且记账被清除，
    /// 比「不能处置」严重得多。动作路径已改用 OpenVerified 共用同一句柄（见 Execute）。
    /// </summary>
    internal static bool IsStillSameProcess(ProcessInfo p)
    {
        if (p.Pid <= 0 || p.StartTime == DateTime.MinValue) return false;
        try
        {
            using var live = System.Diagnostics.Process.GetProcessById(p.Pid);
            return live.StartTime == p.StartTime;
        }
        catch { return false; }   // 进程已退出 / 无法打开 → 不能确认是同一个
    }

    /// <summary>
    /// 定时挂起（v0.15.0）：挂起并登记到期恢复时刻。到期由 ResidentEngine 的
    /// ResumeExpiredSuspends 自动恢复；应用关闭期间到期则在下次启动时补恢复。
    /// 沿用 Execute 的 (pid,startTime) 复检，防止 PID 复用挂错进程。
    /// </summary>
    public ActionResult SuspendTimed(ProcessInfo p, string reason, TimeSpan holdDuration)
    {
        // v0.18.0：身份未知直接拒绝——否则登记的是 MinValue，到期恢复必然失败。
        if (p.StartTime == DateTime.MinValue)
            return new ActionResult(false, "无法确认进程身份（读不到启动时间），已拒绝挂起", false);
        var handle = _procs.OpenVerified(p.Pid, NativeMethods.PROCESS_SUSPEND_RESUME, p.StartTime);
        if (handle == IntPtr.Zero)
            return new ActionResult(false, "目标已不是原进程（PID 可能已被复用或已退出），已拒绝", false);
        try
        {
            _procs.SuspendProcessHandle(handle);
            Suspended.Add(p.Pid, p.StartTime, p.Name, reason, DateTime.UtcNow + holdDuration);
            _audit.Log(new AuditEntry
            {
                Action = "L2Suspend",
                Target = $"{p.Name} (PID {p.Pid})",
                Level = "L2Suspend",
                Result = $"定时挂起 {holdDuration.TotalHours:F0} 小时（{reason}），到期自动恢复"
            });
            return new ActionResult(true, $"已挂起 {holdDuration.TotalHours:F0} 小时，到期自动恢复", false);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "挂起失败: " + ex.Message, false);
        }
        finally
        {
            ProcessService.CloseProcessHandle(handle);
        }
    }

    /// <summary>
    /// 按记录恢复（v0.17.0）：先复检 (pid, startTime)，再调 Resume——定时挂起记录可能
    /// 存活数小时，期间原进程退出、PID 被复用时，绝不能再调 ResumeProcess（会恢复错进程）。
    /// </summary>
    public ActionResult ResumeRecord(MemPilot.Models.SuspendRecord rec)
    {
        // v0.18.0：MinValue 记录无法安全恢复（按 PID 恢复可能恢复错进程）——清除并留审计。
        if (rec.StartTime == DateTime.MinValue)
        {
            Suspended.Remove(rec.Pid);
            _audit.Log(new AuditEntry
            {
                Action = "Resume",
                Target = $"{rec.Name} (PID {rec.Pid})",
                Level = "Resume",
                Result = "跳过：挂起记录没有可靠的启动时间，无法安全恢复（可能恢复错进程），请手动检查该进程"
            });
            return new ActionResult(false, "挂起记录没有可靠的启动时间，无法安全恢复", false);
        }
        if (!IsAliveSame(rec.Pid, rec.StartTime))
        {
            Suspended.Remove(rec.Pid);   // 记账清除（进程已不在）
            return new ActionResult(false, "进程已退出或 PID 已被复用，无需恢复", false);
        }
        return Resume(rec.Pid, rec.Name, rec.StartTime);
    }

    /// <summary>(pid, startTime) 是否仍是同一个活着的进程。</summary>
    public static bool IsAliveSame(int pid, DateTime startUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime() == startUtc.ToUniversalTime();
        }
        catch { return false; }
    }

    /// <summary>租约挂起写进账本的原因（v0.24.0）。稳定标识，恢复孤儿时按它精确匹配。</summary>
    public const string LeaseSuspendReason = "租约挂起";

    /// <summary>
    /// 恢复指定原因留下的挂起记录（v0.24.0）。
    /// 用于"租约期间崩溃 → 内存账本丢失"的孤儿：传 <see cref="LeaseSuspendReason"/> 把它们全部唤醒。
    /// 带 StartTime 走句柄级验证——进程已退出或 PID 被复用的记录会被跳过，不会误恢复别的进程。
    /// </summary>
    public (int restored, int failed) ResumeOrphans(string reason)
    {
        int restored = 0, failed = 0;
        foreach (var r in Suspended.LiveRecords().Where(x => x.Reason == reason).ToList())
        {
            var res = Resume(r.Pid, r.Name, r.StartTime);
            if (res.Success) restored++; else failed++;
        }
        return (restored, failed);
    }

    public ActionResult Resume(int pid, string name, DateTime expectedStartTime = default)
    {
        long before = _procs.GetSystemMemory().AvailablePhysicalBytes;
        string result;
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (expectedStartTime != default)
            {
                // v0.18.0：句柄级验证 + 动作用同一句柄（恢复错进程比不恢复更糟）
                handle = _procs.OpenVerified(pid, NativeMethods.PROCESS_SUSPEND_RESUME, expectedStartTime);
                if (handle == IntPtr.Zero) throw new Exception("进程已退出或 PID 已被复用，无需恢复");
                _procs.ResumeProcessHandle(handle);
            }
            else
            {
                _procs.ResumeProcess(pid);   // UI 明确指定进程的手动恢复
            }
            Suspended.Remove(pid);
            result = "进程已恢复";
        }
        catch (Exception ex) { result = "失败: " + ex.Message; }
        finally { ProcessService.CloseProcessHandle(handle); }
        long after = _procs.GetSystemMemory().AvailablePhysicalBytes;
        _audit.Log(new AuditEntry
        {
            Action = "Resume",
            Target = $"{name} (PID {pid})",
            Level = "Resume",
            Result = result,
            MemoryBeforeBytes = before,
            MemoryAfterBytes = after
        });
        return new ActionResult(!result.StartsWith("失败"), result, false);
    }

    private (bool exited, string message) GracefulClose(ProcessInfo p, int timeoutSec, bool escalateToKill, IntPtr verifiedHandle = default)
    {
        try
        {
            // v0.13.0（四轮复盘）：空句柄快速失败——无窗口进程对 WM_CLOSE 是空操作，
            // 烧满 8 秒超时只会得到必然的失败（实测 4 次全部 Exited:false）。
            if (!_procs.HasVisibleWindows(p.Pid))
                return (false, "进程无可见窗口，无法优雅关闭（WM_CLOSE 无作用对象）；请用回收工作集或手动强制结束");

            _procs.PostCloseToWindows(p.Pid);
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var proc = Process.GetProcessById(p.Pid);
                    using var _ = proc;
                    if (proc.HasExited) return (true, "已通过窗口消息正常关闭");
                }
                catch
                {
                    // process no longer exists == exited
                    return (true, "已通过窗口消息正常关闭");
                }
                Thread.Sleep(500);
            }
            if (escalateToKill)
            {
                try
                {
                    // v0.18.0：升级强杀优先用已验证的句柄——8 秒后按 PID 重开可能已杀错
                    if (verifiedHandle != IntPtr.Zero) _procs.TerminateProcessHandle(verifiedHandle);
                    else _procs.TerminateProcess(p.Pid);
                    return (true, $"优雅关闭超时（{timeoutSec}s），已升级为强制结束");
                }
                catch (Exception ex)
                {
                    return (false, "优雅关闭超时且强制结束失败: " + ex.Message);
                }
            }
            return (false, $"优雅关闭超时（{timeoutSec}s），进程仍在运行");
        }
        finally
        {
            ProcessService.CloseProcessHandle(verifiedHandle);
        }
    }

    // ---- make-room workflow ----

    public MakeRoomPlan BuildPlan(List<ProcessInfo> processes, SystemMemoryInfo mem, ModeProfile profile,
        MakeRoomRequest req, int foregroundPid, HashSet<int> audioPids,
        IReadOnlyDictionary<string, double>? historyWeights = null)
    {
        const long GB = 1024L * 1024 * 1024;
        var plan = new MakeRoomPlan
        {
            AvailableBeforeBytes = mem.AvailablePhysicalBytes,
            CommitFreeBeforeBytes = mem.TotalCommitLimitBytes - mem.TotalCommittedBytes
        };
        plan.PhysicalGapBytes = Math.Max(0, (long)(req.TargetAvailableGB * GB) - mem.AvailablePhysicalBytes);
        plan.CommitGapBytes = req.EnsureCommitToo
            ? Math.Max(0, (long)(req.TargetCommitFreeGB * GB) - plan.CommitFreeBeforeBytes)
            : 0;

        bool modeAllowsKill = profile.AllowedActions.Contains(CleanupActionLevel.L4ForceKill);
        bool allowKill = req.AllowKill && modeAllowsKill;

        var pool = new List<(ProcessInfo p, CleanupActionLevel action, long phys, long commit, string why, int risk, int children)>();
        foreach (var p in processes)
        {
            if (p.IsProtected || p.IsSystemProcess || p.IsService) continue;
            if (p.Category is ProcessCategory.Unknown or ProcessCategory.SystemCritical
                or ProcessCategory.SecuritySoftware or ProcessCategory.AntiCheat or ProcessCategory.Driver
                // v0.22.0：vmmem 一族永不进 make-room 候选（L4 会直接杀掉 WSL/Hyper-V 的内存宿主）
                or ProcessCategory.ContainerVm) continue;
            if (p.IsWhitelisted) continue;
            if (p.Pid == foregroundPid || p.AudioActive || audioPids.Contains(p.Pid) || p.IsSuspended) continue;
            if (p.StartTime != DateTime.MinValue && (DateTime.Now - p.StartTime).TotalSeconds < 60) continue;

            bool modeClean = profile.CleanPatterns.Any(pat =>
                p.Name.Contains(pat, StringComparison.OrdinalIgnoreCase) ||
                p.ExecutablePath.Contains(pat, StringComparison.OrdinalIgnoreCase));
            bool idle = p.IdleMinutes > 0;

            string childNote = p.ChildPids.Count > 0 ? $"（含 {p.ChildPids.Count} 个子进程，关闭将连带影响它们）" : "";
            bool junk = p.Category == ProcessCategory.Bloatware;
            if (p.HasVisibleWindow)
            {
                pool.Add((p, CleanupActionLevel.L3GracefulClose, p.Memory.PrivateWorkingSetBytes, p.Memory.PrivateBytes,
                    (junk ? "垃圾常驻，" : "") + (modeClean ? "模式清理名单 + 有窗口 → 优雅关闭" : idle ? "空闲且有窗口 → 优雅关闭" : "有窗口 → 优雅关闭（会先尝试正常退出）") + childNote,
                    junk || modeClean ? 0 : idle ? 1 : 2, p.ChildPids.Count));
            }
            else if (allowKill)
            {
                pool.Add((p, CleanupActionLevel.L4ForceKill, p.Memory.PrivateWorkingSetBytes, p.Memory.PrivateBytes,
                    (junk ? "垃圾常驻，" : "") + (modeClean ? "模式清理名单 + 无窗口 → 直接结束" : "无窗口无音频 → 结束") + childNote,
                    junk || modeClean ? 0 : 2, p.ChildPids.Count));
            }
            else if (req.IncludeTrim && p.Memory.PrivateWorkingSetBytes > 64 * 1024 * 1024)
            {
                pool.Add((p, CleanupActionLevel.L1Trim, p.Memory.PrivateWorkingSetBytes / 2, 0,
                    "无窗口（未允许结束）→ 仅回收工作集（效果弱）", 0, p.ChildPids.Count));
            }
        }

        // rank: mode-relevant & low risk first, then historical weight (recurring consumers
        // before one-off apps — the "usual sacrifice" ordering), then bigger honest reclaim.
        double Hist(ProcessInfo p) => historyWeights != null && historyWeights.TryGetValue(p.Name, out var w) ? w : 0;
        var chosen = new List<MakeRoomPlanItem>();
        long needPhys = plan.PhysicalGapBytes;
        long needCommit = plan.CommitGapBytes;
        foreach (var c in pool.OrderByDescending(c => c.risk == 0).ThenBy(c => c.risk)
                              .ThenByDescending(c => Hist(c.p))
                              .ThenByDescending(c => Math.Max(c.phys, c.commit / 2)))
        {
            bool stillNeeded = needPhys > 0 || needCommit > 0;
            if (!stillNeeded && req.IncludeTrim && chosen.Count > 0) break;
            if (!stillNeeded) break;
            chosen.Add(new MakeRoomPlanItem
            {
                Process = c.p,
                Action = c.action,
                EstimatedPhysicalReclaimBytes = c.phys,
                EstimatedCommitReclaimBytes = c.commit,
                Rationale = c.why,
                ChildCount = c.children,
                KillIfCloseFails = c.action == CleanupActionLevel.L3GracefulClose && allowKill
            });
            needPhys -= c.phys;
            needCommit -= c.commit;
        }
        plan.Items = chosen;
        return plan;
    }

    public async Task<MakeRoomResult> ExecutePlanAsync(MakeRoomPlan plan, int gracefulTimeoutSec, CancellationToken ct)
    {
        var memBefore = _procs.GetSystemMemory();
        var result = new MakeRoomResult
        {
            AvailableBeforeBytes = memBefore.AvailablePhysicalBytes,
            CommitFreeBeforeBytes = memBefore.TotalCommitLimitBytes - memBefore.TotalCommittedBytes
        };
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            bool escalate = item.Action == CleanupActionLevel.L3GracefulClose && item.KillIfCloseFails;
            var r = Execute(item.Process, item.Action, gracefulTimeoutSec, escalate);
            // v0.11.0：逐项回填结果——执行后计划表保留供复查（原来做完就清空，只能去审计页考古）
            item.ResultText = r.Success ? "成功" : (r.Exited ? "已退出" : $"失败：{r.Message}");
            result.Lines.Add($"{item.ActionText}  {item.Process.Name} (PID {item.Process.Pid})  →  {r.Message}");
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        // realize the reclaim: closed processes leave pages in the low-priority standby list
        if (_memCleaner.IsAdmin())
        {
            try
            {
                _memCleaner.CleanAreas(new List<MemoryArea> { MemoryArea.StandbyListLowPriority });
                result.Lines.Add("已清理低优先级备用列表以兑现回收");
            }
            catch { }
        }
        // second measurement after a settle delay — standby list does not drain instantly
        await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        var memAfter = _procs.GetSystemMemory();
        result.AvailableAfterBytes = memAfter.AvailablePhysicalBytes;
        result.CommitFreeAfterBytes = memAfter.TotalCommitLimitBytes - memAfter.TotalCommittedBytes;
        _audit.Log(new AuditEntry
        {
            Action = "MakeRoom",
            Target = $"{plan.Items.Count} 项计划",
            Level = "Plan",
            Result = result.SummaryText,
            MemoryBeforeBytes = result.AvailableBeforeBytes,
            MemoryAfterBytes = result.AvailableAfterBytes,
            Details = string.Join("; ", result.Lines)
        });
        return result;
    }
}
