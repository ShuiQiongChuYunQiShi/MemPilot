using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 决策日志噪声判定（v0.20.0）。
///
/// 数据依据（10-30 → 11-23 的 decisions.json，5,000 条）：JunkAdvice 占 87.7%（4,387 条），
/// GrowthWatch 次之。它们对排障有价值，但对"我点了什么、系统动过什么"是纯噪声。
///
/// 注意 <c>JunkAdvice</c> Kind 混了两类记录：引擎的「建议/无候选」（噪声）与用户的
/// 「挂起/忽略」、到期「自动恢复」（真实动作，绝不能藏）。所以判定必须看
/// (Kind, Decision) 组合——按 Kind 一刀切会让用户以为挂起没生效。
///
/// 决策日志内容语言固定为中文（README「Known limits」），这里的文案与写入侧一致。
/// </summary>
public static class DecisionLogFilter
{
    /// <summary>引擎发出的建议（默认折叠）。</summary>
    public const string DecisionAdvice = "建议";
    /// <summary>v0.17.0 托盘模式回滚记录，仅历史数据里存在（默认折叠）。</summary>
    public const string DecisionSkipped = "跳过";
    /// <summary>设置页「立即检查」无候选留痕（默认折叠）。</summary>
    public const string DecisionNoCandidate = "无候选";

    /// <summary>整类默认折叠的 Kind（增长观察只是提示，不是动作）。</summary>
    public static readonly IReadOnlySet<string> NoiseKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GrowthWatch" };

    /// <summary>JunkAdvice 中属于噪声的 Decision（其余如挂起/自动恢复/忽略照常显示）。</summary>
    public static readonly IReadOnlySet<string> JunkAdviceNoiseDecisions =
        new HashSet<string>(StringComparer.Ordinal) { DecisionAdvice, DecisionSkipped, DecisionNoCandidate };

    /// <summary>这条记录是否属于"建议/观察"类噪声。</summary>
    public static bool IsNoise(DecisionEntry? entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Kind)) return false;
        if (NoiseKinds.Contains(entry.Kind)) return true;
        return string.Equals(entry.Kind, "JunkAdvice", StringComparison.OrdinalIgnoreCase)
               && JunkAdviceNoiseDecisions.Contains(entry.Decision ?? "");
    }

    /// <summary>
    /// 按开关与 Kind 过滤（v0.22.0 增加 kind）：
    /// showNoise=false 时剔除噪声；kind 非空时只保留该 Kind（大小写不敏感）。两者是 AND 关系。
    /// </summary>
    public static IEnumerable<DecisionEntry> Filter(IEnumerable<DecisionEntry> entries, bool showNoise, string? kind = null)
    {
        var q = showNoise ? entries : entries.Where(e => !IsNoise(e));
        if (!string.IsNullOrWhiteSpace(kind))
            q = q.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
        return q;
    }

    /// <summary>Kind 下拉第一项（全部）。</summary>
    public const string AllKinds = "";
}
