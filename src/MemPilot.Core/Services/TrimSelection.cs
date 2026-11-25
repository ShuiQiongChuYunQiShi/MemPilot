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
        IEnumerable<ProcessInfo> procs, int foregroundPid, IReadOnlySet<int> audioPids,
        IReadOnlySet<int>? excludedPids = null)
    {
        var result = new List<ProcessInfo>();
        if (procs == null) return result;
        var now = DateTime.Now;

        foreach (var p in procs)
        {
            if (!IsSafe(p, foregroundPid, audioPids, now, excludedPids)) continue;
            result.Add(p);
        }
        // 优先压占用大的（回收更多），但仍是"安全子集"内部排序
        return result.OrderByDescending(x => x.Memory.PrivateWorkingSetBytes).ToList();
    }

    /// <summary>
    /// 进程为什么不能进处置候选。给"预览/诊断"用（v0.23.0 的 /api/plan 要让调用方看到
    /// 有多少进程被安全边界挡住），粒度按"对 agent 有意义"划分，不追求覆盖每个 if。
    ///
    /// 关键：这不是第二份判定。IsSafe 与 ExplainUnsafe 共用同一实现，
    /// 避免 v0.9.2 收敛过的"6 份安全判定互相漂移"重演。
    /// </summary>
    public enum TrimReject
    {
        /// <summary>可以处置。</summary>
        None = 0,
        /// <summary>空对象（防御性，视为不可处置）。</summary>
        Invalid,
        /// <summary>MemPilot 自己。</summary>
        Self,
        /// <summary>租约调用方自身或其子树（硬不变式）。</summary>
        Caller,
        /// <summary>受保护/系统/服务/白名单/模式保留/已挂起。</summary>
        Protected,
        /// <summary>分类上不能动：系统/驱动/安全/反作弊/游戏/容器/Unknown。</summary>
        Category,
        /// <summary>用途是工作/娱乐/AI。</summary>
        Workload,
        /// <summary>当前正在被使用：前台/发声/刚启动/CPU 或 IO 忙。</summary>
        InUse,
    }

    /// <summary>单个进程是否允许被安全压缩（IsSafe 的等价入口，保留旧签名）。</summary>
    public static bool IsSafe(ProcessInfo p, int foregroundPid, IReadOnlySet<int> audioPids, DateTime now,
        IReadOnlySet<int>? excludedPids = null)
        => ExplainUnsafe(p, foregroundPid, audioPids, now, excludedPids) == TrimReject.None;

    /// <summary>
    /// 判定并给出拒绝原因。唯一的判定实现——<see cref="IsSafe"/> 只是它的布尔投影。
    /// </summary>
    public static TrimReject ExplainUnsafe(ProcessInfo p, int foregroundPid, IReadOnlySet<int> audioPids,
        DateTime now, IReadOnlySet<int>? excludedPids = null)
    {
        if (p == null) return TrimReject.Invalid;

        // v0.18.0：永不压缩 MemPilot 自己——托盘模式下它满足"无窗口、低 CPU"，
        // 旧实现只靠 CleanupEngine.TrimWorkingSets 的 self 判断兜底，筛选器本身会把它放进候选集。
        if (p.Pid == Environment.ProcessId) return TrimReject.Self;

        // v0.23.0：调用方及其子树永不处置。放在最前面（先于保护/分类判断），
        // 因为这是一条"无论分类如何都不例外"的硬不变式。
        if (excludedPids != null && excludedPids.Contains(p.Pid)) return TrimReject.Caller;

        // ① 保护类身份：一律不动
        if (p.IsProtected || p.IsSystemProcess || p.IsService) return TrimReject.Protected;
        if (p.IsWhitelisted || p.ModeKeep || p.IsSuspended) return TrimReject.Protected;

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
            // v0.22.0：容器/VM 宿主（vmmemWSL 等）压缩工作集 = 把整个 WSL 换出到磁盘，
            // 用户下一次在 WSL 里编译/跑模型会先卡几秒读回。这类内存不是"闲置"，不该动。
            case ProcessCategory.ContainerVm:
            case ProcessCategory.Unknown:   // 看不懂的，不碰
                return TrimReject.Category;
        }

        // ③ 用途：工作/娱乐/AI 都是"用户正在用"的东西
        if (p.Workload is WorkloadKind.Work or WorkloadKind.Ai or WorkloadKind.Entertainment)
            return TrimReject.Workload;

        // ④ 当前正在被使用
        if (p.Pid == foregroundPid || p.AudioActive) return TrimReject.InUse;
        if (audioPids != null && audioPids.Contains(p.Pid)) return TrimReject.InUse;

        // ⑤ 刚启动（可能正在初始化/编译/加载模型）
        if (p.StartTime != DateTime.MinValue && (now - p.StartTime).TotalSeconds < MinAgeSeconds)
            return TrimReject.InUse;

        // ⑥ 正在干活（CPU 或 IO）
        if (p.Memory.CpuPercent >= MaxCpuPercent) return TrimReject.InUse;
        if (p.Memory.IoReadRateBytesPerSec + p.Memory.IoWriteRateBytesPerSec >= MaxIoBytesPerSec)
            return TrimReject.InUse;

        return TrimReject.None;
    }
}
