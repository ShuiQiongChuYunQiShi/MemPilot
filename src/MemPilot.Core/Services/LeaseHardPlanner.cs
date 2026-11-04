using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>硬租约策略：soft 只压缩；hard 允许在"安全子集"里挂起进程（可恢复）。</summary>
public enum LeasePolicy
{
    Soft,
    Hard,
}

/// <summary>一次硬租约里被挂起的进程（归还时逐个恢复；PID+StartTime 防复用）。</summary>
public sealed class LeaseSuspendedProcess
{
    public int Pid { get; set; }
    public DateTime StartTime { get; set; }
    public string Name { get; set; } = "";
    public long PrivateBytes { get; set; }
    public bool Restored { get; set; }
    public string RestoreNote { get; set; } = "";
}

/// <summary>
/// 硬租约的挂起选择与恢复编排（v0.8.0 M2，纯函数 + 状态）。
///
/// 为什么需要：软租约（压缩工作集）只能把页面挪到页面文件，对"提交内存"和"真的需要物理内存"的
/// 大任务帮不上——实测压缩后提交余量不变。硬租约用 L2 挂起真正冻结进程并让出物理内存，
/// 归还时再恢复，这是"给工作让路"最直接的兑现方式。
///
/// 安全边界（比软租约更严）：只在 TrimSelection 的安全子集里挑；额外要求"足够闲"——
/// 无窗口 或 空闲 ≥10 分钟，避免挂起用户正在看但没在操作的程序。用户明确不要工作进程被回收，
/// 所以 Work/AI/娱乐用途、游戏、前台、发声、受保护的进程一律不进候选。
/// </summary>
public static class LeaseHardPlanner
{
    /// <summary>挂起的额外门槛：空闲至少这么久（配合"无窗口或已空闲"双条件）。</summary>
    public const double MinIdleMinutes = 10;

    /// <summary>
    /// 从当前快照挑出可挂起的候选，按可释放提交内存降序排列。
    /// 返回的列表供调用方按目标额度截断后实际挂起。
    /// </summary>
    public static List<ProcessInfo> PickSuspendCandidates(
        IEnumerable<ProcessInfo> procs, int foregroundPid, IReadOnlySet<int> audioPids, DateTime now)
    {
        var result = new List<ProcessInfo>();
        if (procs == null) return result;

        foreach (var p in procs)
        {
            if (!TrimSelection.IsSafe(p, foregroundPid, audioPids, now)) continue;
            // 额外：必须"足够闲"——无窗口，或已空闲 ≥ MinIdleMinutes
            bool idle = !p.HasVisibleWindow || p.IdleMinutes >= MinIdleMinutes;
            if (!idle) continue;
            result.Add(p);
        }

        return result
            .OrderByDescending(p => p.Memory.PrivateBytes)   // 挂起主要释放提交内存
            .ToList();
    }

    /// <summary>按目标缺口截断候选：累计私有提交达到 needBytes 即停（最多 maxCount 个，避免一刀切挂一片）。</summary>
    public static List<ProcessInfo> TakeForShortfall(List<ProcessInfo> candidates, long needBytes, int maxCount = 8)
    {
        var picked = new List<ProcessInfo>();
        if (candidates == null || needBytes <= 0) return picked;
        long sum = 0;
        foreach (var p in candidates)
        {
            if (picked.Count >= maxCount) break;
            picked.Add(p);
            sum += Math.Max(0, p.Memory.PrivateBytes);
            if (sum >= needBytes) break;
        }
        return picked;
    }
}
