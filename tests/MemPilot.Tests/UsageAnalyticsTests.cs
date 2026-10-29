using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

public class UsageAnalyticsTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static UsageSample S(params (string name, long gb)[] tops)
    {
        var s = new UsageSample { T = DateTime.UtcNow, Total = 32 * Gb, Avail = 8 * Gb };
        foreach (var (name, gb) in tops)
            s.Top.Add(new UsageTopProc { N = name, W = gb * Gb });
        return s;
    }

    [Fact]
    public void RecurringBigProcess_Outranks_RareSmallProcess()
    {
        var samples = new List<UsageSample>();
        for (int gb = 1; gb <= 10; gb++) samples.Add(S(("python", gb), ("rareapp", 2)));
        var weights = UsageAnalytics.ComputeProcessWeights(samples);

        Assert.True(weights["python"] > weights["rareapp"], $"python={weights["python"]}, rare={weights["rareapp"]}");
        Assert.True(weights["python"] > 5, $"python weight too low: {weights["python"]}");
    }

    [Fact]
    public void AbsentProcess_HasNoWeight_EmptyInput_EmptyResult()
    {
        var weights = UsageAnalytics.ComputeProcessWeights(new[] { S(("a", 1)) });
        Assert.False(weights.ContainsKey("ghost"));
        Assert.Empty(UsageAnalytics.ComputeProcessWeights(Array.Empty<UsageSample>()));
    }
}
