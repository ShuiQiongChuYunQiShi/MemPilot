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

    public ActionResult Execute(ProcessInfo p, CleanupActionLevel level, int gracefulTimeoutSec, bool escalateToKill = false)
    {
        long before = _procs.GetSystemMemory().AvailablePhysicalBytes;
        string result;
        bool exited = false;
        try
        {
            switch (level)
            {
                case CleanupActionLevel.L1Trim:
                    _procs.TrimWorkingSet(p.Pid);
                    result = "工作集已回收";
                    break;
                case CleanupActionLevel.L2Suspend:
                    _procs.SuspendProcess(p.Pid);
                    Suspended.Add(p.Pid, p.StartTime, p.Name, "手动挂起");
                    result = "进程已挂起（可在“已挂起”中恢复）";
                    break;
                case CleanupActionLevel.L3GracefulClose:
                    var r = GracefulClose(p, gracefulTimeoutSec, escalateToKill);
                    result = r.message; exited = r.exited;
                    break;
                case CleanupActionLevel.L4ForceKill:
                    _procs.TerminateProcess(p.Pid);
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

    public ActionResult Resume(int pid, string name)
    {
        long before = _procs.GetSystemMemory().AvailablePhysicalBytes;
        string result;
        try
        {
            _procs.ResumeProcess(pid);
            Suspended.Remove(pid);
            result = "进程已恢复";
        }
        catch (Exception ex) { result = "失败: " + ex.Message; }
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

    private (bool exited, string message) GracefulClose(ProcessInfo p, int timeoutSec, bool escalateToKill)
    {
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
                _procs.TerminateProcess(p.Pid);
                return (true, $"优雅关闭超时（{timeoutSec}s），已升级为强制结束");
            }
            catch (Exception ex)
            {
                return (false, "优雅关闭超时且强制结束失败: " + ex.Message);
            }
        }
        return (false, $"优雅关闭超时（{timeoutSec}s），进程仍在运行");
    }

    // ---- make-room workflow ----

    public MakeRoomPlan BuildPlan(List<ProcessInfo> processes, SystemMemoryInfo mem, ModeProfile profile,
        MakeRoomRequest req, int foregroundPid, HashSet<int> audioPids)
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
                or ProcessCategory.SecuritySoftware or ProcessCategory.AntiCheat or ProcessCategory.Driver) continue;
            if (p.IsWhitelisted) continue;
            if (p.Pid == foregroundPid || p.AudioActive || audioPids.Contains(p.Pid) || p.IsSuspended) continue;
            if (p.StartTime != DateTime.MinValue && (DateTime.Now - p.StartTime).TotalSeconds < 60) continue;

            bool modeClean = profile.CleanPatterns.Any(pat =>
                p.Name.Contains(pat, StringComparison.OrdinalIgnoreCase) ||
                p.ExecutablePath.Contains(pat, StringComparison.OrdinalIgnoreCase));
            bool idle = p.IdleMinutes > 0;

            string childNote = p.ChildPids.Count > 0 ? $"（含 {p.ChildPids.Count} 个子进程，关闭将连带影响它们）" : "";
            if (p.HasVisibleWindow)
            {
                pool.Add((p, CleanupActionLevel.L3GracefulClose, p.Memory.PrivateWorkingSetBytes, p.Memory.PrivateBytes,
                    (modeClean ? "模式清理名单 + 有窗口 → 优雅关闭" : idle ? "空闲且有窗口 → 优雅关闭" : "有窗口 → 优雅关闭（会先尝试正常退出）") + childNote,
                    modeClean ? 0 : idle ? 1 : 2, p.ChildPids.Count));
            }
            else if (allowKill)
            {
                pool.Add((p, CleanupActionLevel.L4ForceKill, p.Memory.PrivateWorkingSetBytes, p.Memory.PrivateBytes,
                    (modeClean ? "模式清理名单 + 无窗口 → 直接结束" : "无窗口无音频 → 结束") + childNote,
                    modeClean ? 0 : 2, p.ChildPids.Count));
            }
            else if (req.IncludeTrim && p.Memory.PrivateWorkingSetBytes > 64 * 1024 * 1024)
            {
                pool.Add((p, CleanupActionLevel.L1Trim, p.Memory.PrivateWorkingSetBytes / 2, 0,
                    "无窗口（未允许结束）→ 仅回收工作集（效果弱）", 0, p.ChildPids.Count));
            }
        }

        // rank: mode-relevant & low risk first, bigger honest reclaim first
        var chosen = new List<MakeRoomPlanItem>();
        long needPhys = plan.PhysicalGapBytes;
        long needCommit = plan.CommitGapBytes;
        foreach (var c in pool.OrderByDescending(c => c.risk == 0).ThenBy(c => c.risk)
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
