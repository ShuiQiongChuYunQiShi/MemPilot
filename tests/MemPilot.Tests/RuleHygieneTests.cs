using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

public class RuleHygieneTests : IDisposable
{
    private readonly string _dir;

    public RuleHygieneTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-hygiene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Report_CountsHitsKeepsPromotions_AndDeadRules()
    {
        var decisions = new List<DecisionEntry>
        {
            new() { Kind = "JunkSweep", Target = "wetype_update (PID 1)", TimestampUtc = DateTime.UtcNow.AddHours(-1) },
            new() { Kind = "JunkSweep", Target = "wetype_update (PID 1)", TimestampUtc = DateTime.UtcNow.AddHours(-2) },
            new() { Kind = "PromoteRule", Target = "foo", TimestampUtc = DateTime.UtcNow.AddHours(-1) },
            new() { Kind = "GrowthWatch", Target = "bar (PID 2)", TimestampUtc = DateTime.UtcNow.AddHours(-1) },
        };
        var feedback = new List<FeedbackEntry>
        {
            new() { ProcessName = "keepme", MarkKeep = true },
            new() { ProcessName = "keepme", MarkKeep = true }, // duplicate collapses
        };
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));

        var r = RuleHygiene.Build(decisions, feedback, ledger, DateTime.UtcNow.AddDays(-7));

        Assert.Equal(1, r.ActiveRuleHits);
        Assert.True(r.DeadRuleCandidates > 0, "empty ledger means every rule is a dead candidate");
        Assert.Equal(1, r.UserKeeps);
        Assert.Equal(1, r.UserPromotions);
        Assert.Contains("wetype_update×2", r.Text);
        Assert.Contains("keepme", r.Text);
    }

    [Fact]
    public void ObservedJunkRules_AreNotDeadCandidates()
    {
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        var p = new ProcessInfo { Pid = 1, Name = "wetype_update", Memory = new MemoryMetrics() };
        ledger.Observe(new List<ProcessInfo> { p });

        var r = RuleHygiene.Build(Array.Empty<DecisionEntry>(), Array.Empty<FeedbackEntry>(),
            ledger, DateTime.UtcNow.AddDays(-7));

        Assert.True(r.DeadRuleCandidates < ClassificationEngine.JunkRuleNames.Count,
            "an observed rule must not count as dead");
        Assert.DoesNotContain("wetype_update", r.Text.Split('；')[1]); // 死规则段落不含它
    }
}
