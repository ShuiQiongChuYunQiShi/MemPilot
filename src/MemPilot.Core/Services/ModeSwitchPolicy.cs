namespace MemPilot.Services;

/// <summary>模式切换判定结果。</summary>
public readonly struct ModeSwitchDecision
{
    public ModeSwitchDecision(bool apply, bool storm)
    { Apply = apply; Storm = storm; }
    /// <summary>是否真正执行切换（false = 被去重抑制）。</summary>
    public bool Apply { get; }
    /// <summary>10 分钟内切换过于频繁（只记录，不阻断）。</summary>
    public bool Storm { get; }
}

/// <summary>
/// 模式切换归因与去重（v0.5.0，纯函数）。
///
/// 数据依据：51 条审计全部标 Manual，其中 15 次挤在 11-01 02:21–02:41——
/// 自动切换明明是关的，说明要么是 UI 重复触发，要么有别处调用 setter。
/// 这里把"同一目标 60 秒内重复"抑制掉并记录，把"10 分钟内 &gt;5 次"标成 Storm（只告警不阻断），
/// 配合调用方补的 source 标签，下一轮数据就能定位来源。
/// </summary>
public static class ModeSwitchPolicy
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StormWindow = TimeSpan.FromMinutes(10);
    public const int StormThreshold = 5;

    public static ModeSwitchDecision Decide(
        string? lastTarget, string? newTarget, DateTime lastSwitchUtc, DateTime nowUtc,
        IReadOnlyList<DateTime>? recentSwitchTimes)
    {
        bool duplicate = !string.IsNullOrEmpty(lastTarget) &&
                         string.Equals(lastTarget, newTarget, StringComparison.Ordinal) &&
                         lastSwitchUtc != DateTime.MinValue &&
                         nowUtc - lastSwitchUtc < DuplicateWindow;

        int recent = 0;
        if (recentSwitchTimes != null)
        {
            foreach (var t in recentSwitchTimes)
                if (nowUtc - t <= StormWindow) recent++;
        }

        return new ModeSwitchDecision(!duplicate, recent + 1 > StormThreshold);
    }
}
