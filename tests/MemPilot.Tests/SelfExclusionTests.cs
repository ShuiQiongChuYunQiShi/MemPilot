using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.18.0：MemPilot 绝不能处置（或建议处置）自己。
/// 托盘模式下它无可见窗口、CPU 很低，正好满足"闲置"的形态；旧实现的筛选器会把它
/// 放进候选集，只靠 CleanupEngine.TrimWorkingSets 的 self 判断兜底。
/// </summary>
public class SelfExclusionTests
{
    private static readonly DateTime Now = DateTime.Now;

    private static ProcessInfo Self() => new()
    {
        Pid = Environment.ProcessId,
        Name = "MemPilot",
        Category = ProcessCategory.ThirdPartyApp,
        StartTime = Now.AddHours(-2),
        Memory = new MemoryMetrics { CpuPercent = 0.2, PrivateWorkingSetBytes = 80L * 1024 * 1024 }
    };

    [Fact]
    public void TrimSelection_NeverTrimSelf()
    {
        Assert.False(TrimSelection.IsSafe(Self(), 0, new HashSet<int>(), Now));
    }

    [Fact]
    public void JunkSafetyFilter_NeverEligibleForSelf()
    {
        Assert.False(JunkSafetyFilter.IsEligible(
            Self(), 0, new HashSet<int>(), Array.Empty<string>(), 0, Now));
    }

    [Fact]
    public void JunkSweeper_PlanNeverContainsSelf()
    {
        var plan = JunkSweeper.BuildPlan(
            new List<ProcessInfo> { Self() }, 0, new HashSet<int>(), Array.Empty<string>(), 0);
        Assert.Empty(plan);
    }
}
