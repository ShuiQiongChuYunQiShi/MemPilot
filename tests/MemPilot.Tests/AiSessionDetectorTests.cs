using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// AI 会话识别（v0.5.0）：用 11-01 真机曲线做夹具。
/// 关键约束：爆发之间的单点回落不能当成结束；DeltaForce 的 5–6.36GB 不能当成 AI 会话。
/// </summary>
public class AiSessionDetectorTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 2, 10, 0, DateTimeKind.Utc);
    private const long Gib = 1024L * 1024 * 1024;
    private const long Total = 17103323136;

    private static List<GpuSampleProc> Top(string name, int pid, long dedicatedGb, long sharedGb = 0)
        => new() { new GpuSampleProc { N = name, I = pid, D = dedicatedGb * Gib, S = sharedGb * Gib } };

    /// <summary>真机 02:10–02:47：7 段爆发，中间夹 0.76GB 单点回落；每次爆发换 python PID。</summary>
    private static readonly long[] RealBurstSeries =
    {
        1, 15, 15, 0, 11, 14, 14, 0, 14, 15, 15, 15, 0, 0, 0, 0
    };

    [Fact]
    public void RealBurstSeries_IsOneSession_NotSeven()
    {
        var d = new AiSessionDetector();
        int started = 0, ended = 0;
        AiSessionSummary? done = null;

        for (int i = 0; i < RealBurstSeries.Length; i++)
        {
            var used = RealBurstSeries[i] * Gib;
            var update = d.Push(used, Total, T0.AddSeconds(30 * i), Top("python", 1000 + i, RealBurstSeries[i]), commitFreePct: 5);
            if (update.Event == AiSessionEvent.Started) started++;
            if (update.Event == AiSessionEvent.Ended) { ended++; done = update.Summary; }
        }

        Assert.Equal(1, started);
        Assert.Equal(1, ended);
        Assert.NotNull(done);
        Assert.Equal(15 * Gib, done!.PeakUsedBytes);
        Assert.Equal("python", done.DominantName);
        Assert.True(done.PidCount >= 2, $"应记录到多个 PID（生成任务每次换 worker），实际 {done.PidCount}");
        Assert.True(done.Duration.TotalMinutes >= 5);
    }

    [Fact]
    public void GameAtSixGb_DoesNotStartSession()
    {
        var d = new AiSessionDetector();
        for (int i = 0; i < 20; i++)
        {
            var update = d.Push((5 + i % 3) * Gib, Total, T0.AddSeconds(30 * i), Top("DeltaForce", 42, 5));
            Assert.Equal(AiSessionEvent.None, update.Event);
        }
        Assert.False(d.Active);
    }

    [Fact]
    public void NeedsThreeOfFive_ToStart()
    {
        var d = new AiSessionDetector();
        Assert.Equal(AiSessionEvent.None, d.Push(10 * Gib, Total, T0).Event);
        Assert.Equal(AiSessionEvent.None, d.Push(1 * Gib, Total, T0.AddSeconds(30)).Event);
        Assert.Equal(AiSessionEvent.None, d.Push(10 * Gib, Total, T0.AddSeconds(60)).Event);
        Assert.Equal(AiSessionEvent.Started, d.Push(10 * Gib, Total, T0.AddSeconds(90)).Event);
    }

    [Fact]
    public void SingleDip_DoesNotEndSession_ThreeLowsDo()
    {
        var d = new AiSessionDetector();
        foreach (var gb in new long[] { 15, 15, 15 }) d.Push(gb * Gib, Total, T0.AddSeconds(30 * gb));
        Assert.True(d.Active);

        Assert.Equal(AiSessionEvent.None, d.Push(1 * Gib, Total, T0.AddSeconds(120)).Event);   // 单点回落
        Assert.Equal(AiSessionEvent.None, d.Push(15 * Gib, Total, T0.AddSeconds(150)).Event);
        Assert.Equal(AiSessionEvent.None, d.Push(1 * Gib, Total, T0.AddSeconds(180)).Event);   // 第 2 个低位
        var end = d.Push(1 * Gib, Total, T0.AddSeconds(210));                                   // 第 3 个低位（窗口内 3/5）
        Assert.Equal(AiSessionEvent.Ended, end.Event);
        Assert.NotNull(end.Summary!.EndUtc);
    }

    [Fact]
    public void TracksPeakSharedAndCommit()
    {
        var d = new AiSessionDetector();
        d.Push(9 * Gib, Total, T0, Top("python", 1, 8, 2), commitFreePct: 12);
        d.Push(9 * Gib, Total, T0.AddSeconds(30), Top("python", 1, 8, 2), commitFreePct: 12);
        d.Push(15 * Gib, Total, T0.AddSeconds(60), Top("python", 1, 14, 11), commitFreePct: 3);
        var s = d.Current!;
        Assert.Equal(15 * Gib, s.PeakUsedBytes);
        Assert.Equal(11 * Gib, s.PeakSharedBytes);
        Assert.Equal(3, s.PeakCommitFreePct);
    }

    [Fact]
    public void AfterEnd_ResetsSoStaleHighSamplesDoNotRetrigger()
    {
        var d = new AiSessionDetector();
        for (int i = 0; i < 3; i++) d.Push(15 * Gib, Total, T0.AddSeconds(30 * i));
        for (int i = 3; i < 6; i++) d.Push(1 * Gib, Total, T0.AddSeconds(30 * i));
        Assert.False(d.Active);
        // 紧接着一个高位样本不应立刻重新开始（窗口已清空，需要重新攒 3/5）
        Assert.Equal(AiSessionEvent.None, d.Push(15 * Gib, Total, T0.AddSeconds(210)).Event);
    }
}

/// <summary>模式切换归因与去重（v0.5.0）：重复切换抑制 + 风暴记录。</summary>
public class ModeSwitchPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 2, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void SameTargetWithinSixtySeconds_IsSuppressed()
    {
        var d = ModeSwitchPolicy.Decide("Development", "Development", Now.AddSeconds(-30), Now, null);
        Assert.False(d.Apply);
    }

    [Fact]
    public void SameTargetAfterWindow_Applies()
    {
        var d = ModeSwitchPolicy.Decide("Development", "Development", Now.AddSeconds(-90), Now, null);
        Assert.True(d.Apply);
    }

    [Fact]
    public void DifferentTarget_Applies()
    {
        var d = ModeSwitchPolicy.Decide("Resident", "Development", Now.AddSeconds(-5), Now, null);
        Assert.True(d.Apply);
    }

    [Fact]
    public void MoreThanFiveInTenMinutes_IsStorm()
    {
        var recent = Enumerable.Range(0, 6).Select(i => Now.AddMinutes(-i)).ToList();
        Assert.True(ModeSwitchPolicy.Decide("Resident", "Development", Now.AddMinutes(-1), Now, recent).Storm);
    }

    [Fact]
    public void OldSwitches_DoNotCountTowardStorm()
    {
        var recent = Enumerable.Range(0, 6).Select(i => Now.AddMinutes(-20 - i)).ToList();
        Assert.False(ModeSwitchPolicy.Decide("Resident", "Development", Now.AddMinutes(-30), Now, recent).Storm);
    }
}
