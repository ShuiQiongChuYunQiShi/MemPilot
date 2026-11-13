using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// "安全子集"工作集压缩（v0.5.0）。
///
/// 用户硬约束：**绝不回收正在工作的进程**。提交内存告急时允许压缩的，只能是"低负载后台"，
/// 一切可能在干活的都要排除：工作/娱乐/AI 用途、游戏/反作弊/系统/驱动/安全软件、前台、发声、
/// 最近启动、CPU 或 IO 正在忙、受保护/白名单/模式保留/已挂起、以及仍然 Unknown 的进程。
///
/// 纯函数：只做筛选，不执行压缩（执行在 CleanupEngine），便于把"排除谁"写成测试。
/// </summary>
public static class TrimSelection
{
    /// <summary>CPU ≥ 此值视为"正在干活"。</summary>
    public const double MaxCpuPercent = 5.0;
    /// <summary>读写合计 ≥ 此值视为"正在干活"（1 MB/s）。</summary>
    public const long MaxIoBytesPerSec = 1024 * 1024;
    /// <summary>启动不足此秒数的不碰（默认高于未使用检测的 120s，避免压到刚启动的构建/生成任务）。</summary>
    public const double MinAgeSeconds = 300;

    public static List<ProcessInfo> BuildSafeTrimSet(
        IEnumerable<ProcessInfo> procs, int foregroundPid, IReadOnlySet<int> audioPids)
    {
        var result = new List<ProcessInfo>();
        if (procs == null) return result;
        var now = DateTime.Now;

        foreach (var p in procs)
        {
            if (!IsSafe(p, foregroundPid, audioPids, now)) continue;
            result.Add(p);
        }
        // 优先压占用大的（回收更多），但仍是"安全子集"内部排序
        return result.OrderByDescending(x => x.Memory.PrivateWorkingSetBytes).ToList();
    }

    /// <summary>单个进程是否允许被安全压缩。抽成公开方法方便诊断/测试逐条断言。</summary>
    public static bool IsSafe(ProcessInfo p, int foregroundPid, IReadOnlySet<int> audioPids, DateTime now)
    {
        if (p == null) return false;

        // v0.18.0：永不压缩 MemPilot 自己——托盘模式下它满足"无窗口、低 CPU"，
        // 旧实现只靠 CleanupEngine.TrimWorkingSets 的 self 判断兜底，筛选器本身会把它放进候选集。
        if (p.Pid == Environment.ProcessId) return false;

        // ① 保护类身份：一律不动
        if (p.IsProtected || p.IsSystemProcess || p.IsService) return false;
        if (p.IsWhitelisted || p.ModeKeep || p.IsSuspended) return false;

        // ② 分类：系统/安全/反作弊/驱动/游戏/AI 都可能"正在被使用"
        switch (p.Category)
        {
            case ProcessCategory.SystemCritical:
            case ProcessCategory.SystemService:
            case ProcessCategory.Driver:
            case ProcessCategory.SecuritySoftware:
            case ProcessCategory.AntiCheat:
            case ProcessCategory.Game:
            case ProcessCategory.GameLauncher:
            case ProcessCategory.AiImageVideo:
            case ProcessCategory.Unknown:   // 看不懂的，不碰
                return false;
        }

        // ③ 用途：工作/娱乐/AI 都是"用户正在用"的东西
        if (p.Workload is WorkloadKind.Work or WorkloadKind.Ai or WorkloadKind.Entertainment) return false;

        // ④ 当前正在被使用
        if (p.Pid == foregroundPid || p.AudioActive) return false;
        if (audioPids != null && audioPids.Contains(p.Pid)) return false;

        // ⑤ 刚启动（可能正在初始化/编译/加载模型）
        if (p.StartTime != DateTime.MinValue && (now - p.StartTime).TotalSeconds < MinAgeSeconds) return false;

        // ⑥ 正在干活（CPU 或 IO）
        if (p.Memory.CpuPercent >= MaxCpuPercent) return false;
        if (p.Memory.IoReadRateBytesPerSec + p.Memory.IoWriteRateBytesPerSec >= MaxIoBytesPerSec) return false;

        return true;
    }
}
