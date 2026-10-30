using MemPilot.Models;

namespace MemPilot.Services;

public record RuleHygieneReport(
    int ActiveRuleHits, int DeadRuleCandidates, int UserKeeps, int UserPromotions, string Text);

/// <summary>
/// 规则卫生审计（P3.4）：垃圾规则命中、死规则候选（行为台账里从未出现 = 可能已卸载/改名）、
/// 用户"保留"覆盖（针对已收录规则的保留需复核是否误报）、用户晋升建议。
/// 每周生成一次综述写决策日志（Kind=RuleAudit）。只报告不修改——规则库的增删永远留给人。
/// </summary>
public static class RuleHygiene
{
    public static RuleHygieneReport Build(
        IEnumerable<DecisionEntry> decisions, IEnumerable<FeedbackEntry> feedback,
        BehaviorLedger ledger, DateTime sinceUtc)
    {
        var hits = decisions.Where(d => d.Kind == "JunkSweep" && d.TimestampUtc >= sinceUtc)
                            .Select(d => d.Target.Split(" (PID")[0])
                            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var rules = ClassificationEngine.JunkRuleNames;
        var dead = rules.Where(r => !ledger.Entries.ContainsKey(r)).OrderBy(x => x).ToList();
        var keeps = feedback.Where(f => f.MarkKeep)
                            .Select(f => f.ProcessName)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int promotions = decisions.Count(d => d.Kind == "PromoteRule" && d.TimestampUtc >= sinceUtc);

        string hitsText = hits.Count == 0 ? "无"
            : string.Join("、", hits.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}×{kv.Value}"));
        string deadText = dead.Count == 0 ? "无" : string.Join("、", dead.Take(10));
        string keepText = keeps.Count == 0 ? "无" : string.Join("、", keeps.Take(8));

        string text = $"规则卫生周报：命中规则 {hits.Count} 条（{hitsText}）；" +
                      $"死规则候选 {dead.Count} 条（{deadText}）；" +
                      $"用户保留覆盖 {keeps.Count} 个（{keepText}，针对已收录规则的保留需复核是否误报）；" +
                      $"晋升建议 {promotions} 条（在决策日志中查看 PromoteRule）";
        return new RuleHygieneReport(hits.Count, dead.Count, keeps.Count, promotions, text);
    }
}
