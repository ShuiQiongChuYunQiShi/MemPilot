namespace MemPilot.Services;

/// <summary>提交兜底动作级别。</summary>
public enum ReliefAction
{
    None,
    /// <summary>一级：清低优先级备用列表（不碰任何进程）。</summary>
    PurgeStandby,
    /// <summary>二级：对"安全子集"做 L1 工作集压缩（等价于温和 trim，不结束进程）。</summary>
    TrimSafe,
}

public readonly struct ReliefInputs
{
    public double CommitFreePct { get; init; }     // 提交余量 / 提交上限 × 100
    public double CommitGrowthGb { get; init; }    // 观察窗口内的提交增长
    public double FreeGb { get; init; }           // 物理可用
    public bool Confirmed { get; init; }          // 连续 2 个采样确认
    public DateTime NowUtc { get; init; }
    public DateTime LastActionUtc { get; init; }
    public ReliefAction LastAction { get; init; }
    public bool ForegroundFullscreen { get; init; }
    public bool AiSessionActive { get; init; }
    public bool GameBusy { get; init; }           // 游戏类进程活跃（CPU/GPU）
}

/// <summary>
/// 提交内存兜底策略（v0.5.0，纯函数）。
///
/// 数据依据：四天 54 次提交告警里 35 次是"余量 &lt;10%"，最紧 0.3%（43.1/43.2 GB）时物理可用仍有 31%——
/// 只看可用内存的旧逻辑（AutoOptimize）在那一刻完全不动手。
///
/// 设计取舍：
/// - 一级只清备用列表（零进程风险）；
/// - 二级才压缩"安全子集"（见 TrimSelection），且必须在 2 分钟后复查仍 &lt;5% 才升级；
/// - 冷却 15 分钟（二级后），一级重复至少间隔 10 分钟；
/// - 前台全屏 / AI 会话活跃 / 游戏进程忙 → 一律不动（用户正在用，压缩只会拖慢）。
/// </summary>
public static class CommitReliefPolicy
{
    public const double CriticalCommitFreePct = 5.0;   // 提交余量 <5% 视为告急
    public const double EscalateCommitFreePct = 5.0;   // 升级到二级的门槛
    public const double SurgeGrowthGb = 8.0;           // 10 分钟涨 8GB
    public const double SurgeCommitFreePct = 25.0;
    public const double SurgeFreeGb = 8.0;
    public static readonly TimeSpan EscalateAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan PurgeRepeat = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TrimCooldown = TimeSpan.FromMinutes(15);

    public static ReliefAction Decide(in ReliefInputs i)
    {
        // 用户正在使用时不动作
        if (i.ForegroundFullscreen || i.AiSessionActive || i.GameBusy) return ReliefAction.None;
        if (!i.Confirmed) return ReliefAction.None;

        bool critical = i.CommitFreePct < CriticalCommitFreePct;
        bool surge = i.CommitGrowthGb >= SurgeGrowthGb &&
                     i.CommitFreePct < SurgeCommitFreePct &&
                     i.FreeGb < SurgeFreeGb;
        if (!critical && !surge) return ReliefAction.None;

        var sinceLast = i.LastActionUtc == DateTime.MinValue
            ? TimeSpan.MaxValue
            : i.NowUtc - i.LastActionUtc;

        if (i.LastAction == ReliefAction.TrimSafe)
            return sinceLast < TrimCooldown ? ReliefAction.None : ReliefAction.PurgeStandby; // 冷却后重新从一级开始

        if (i.LastAction == ReliefAction.PurgeStandby)
        {
            // 刚清过备用列表：2 分钟后复查仍告急才升级压缩
            if (sinceLast < EscalateAfter) return ReliefAction.None;
            if (critical && sinceLast >= EscalateAfter) return ReliefAction.TrimSafe;
            return sinceLast >= PurgeRepeat ? ReliefAction.PurgeStandby : ReliefAction.None;
        }

        return ReliefAction.PurgeStandby;
    }
}
