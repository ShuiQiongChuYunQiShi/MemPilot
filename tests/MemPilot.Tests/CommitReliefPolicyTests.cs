using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 提交内存兜底（v0.5.0）：复刻真机最危险的时刻——11-01 02:30 提交余量 0.3%、物理可用还有 31%。
/// 旧逻辑（只看可用%）在那一刻完全不动手；这里验证分级、升级、冷却与"用户在用就不动"。
/// </summary>
public class CommitReliefPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 2, 30, 0, DateTimeKind.Utc);

    private static ReliefInputs Input(double commitFreePct = 0.3, double growthGb = 0,
        double freeGb = 10.5, bool confirmed = true, DateTime? lastUtc = null,
        ReliefAction last = ReliefAction.None, bool fullscreen = false, bool ai = false, bool gameBusy = false)
        => new()
        {
            CommitFreePct = commitFreePct,
            CommitGrowthGb = growthGb,
            FreeGb = freeGb,
            Confirmed = confirmed,
            NowUtc = Now,
            LastActionUtc = lastUtc ?? DateTime.MinValue,
            LastAction = last,
            ForegroundFullscreen = fullscreen,
            AiSessionActive = ai,
            GameBusy = gameBusy
        };

    [Fact]
    public void RealIncident_CommitAlmostExhausted_TriggersStandbyFirst()
    {
        // 11-01 02:30：提交 43.1/43.2GB（0.3%），物理可用 31%（约 10.5GB）
        Assert.Equal(ReliefAction.PurgeStandby, CommitReliefPolicy.Decide(Input()));
    }

    [Fact]
    public void UnconfirmedSample_DoesNothing()
        => Assert.Equal(ReliefAction.None, CommitReliefPolicy.Decide(Input(confirmed: false)));

    [Fact]
    public void HealthyCommit_DoesNothing()
        => Assert.Equal(ReliefAction.None, CommitReliefPolicy.Decide(Input(commitFreePct: 30, freeGb: 12)));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void UserIsUsingTheMachine_NeverActs(bool fullscreen, bool ai, bool gameBusy)
        => Assert.Equal(ReliefAction.None,
            CommitReliefPolicy.Decide(Input(fullscreen: fullscreen, ai: ai, gameBusy: gameBusy)));

    [Fact]
    public void EscalatesToSafeTrim_OnlyAfterTwoMinutesAndStillCritical()
    {
        // 刚清过备用列表 30 秒 → 不动
        Assert.Equal(ReliefAction.None,
            CommitReliefPolicy.Decide(Input(lastUtc: Now.AddSeconds(-30), last: ReliefAction.PurgeStandby)));
        // 2 分钟后仍 <5% → 升级到安全子集压缩
        Assert.Equal(ReliefAction.TrimSafe,
            CommitReliefPolicy.Decide(Input(lastUtc: Now.AddMinutes(-2), last: ReliefAction.PurgeStandby)));
        // 2 分钟后已缓解（提交 30%）→ 不升级
        Assert.Equal(ReliefAction.None,
            CommitReliefPolicy.Decide(Input(commitFreePct: 30, lastUtc: Now.AddMinutes(-2), last: ReliefAction.PurgeStandby)));
    }

    [Fact]
    public void AfterTrim_CooldownBlocksForFifteenMinutes()
    {
        Assert.Equal(ReliefAction.None,
            CommitReliefPolicy.Decide(Input(lastUtc: Now.AddMinutes(-5), last: ReliefAction.TrimSafe)));
        Assert.Equal(ReliefAction.PurgeStandby,
            CommitReliefPolicy.Decide(Input(lastUtc: Now.AddMinutes(-20), last: ReliefAction.TrimSafe)));
    }

    [Fact]
    public void PurgeRepeatsAtMostEveryTenMinutes_WhenStressIsGrowthNotCritical()
    {
        // 增长型压力（提交余量 20%、10 分钟涨 9GB、可用 5GB）：不会升级成 Trim，只会按 10 分钟重复清理
        var surge = Input(commitFreePct: 20, growthGb: 9, freeGb: 5);
        Assert.Equal(ReliefAction.None,
            CommitReliefPolicy.Decide(surge with { LastActionUtc = Now.AddMinutes(-5), LastAction = ReliefAction.PurgeStandby }));
        Assert.Equal(ReliefAction.PurgeStandby,
            CommitReliefPolicy.Decide(surge with { LastActionUtc = Now.AddMinutes(-11), LastAction = ReliefAction.PurgeStandby }));
    }

    [Fact]
    public void SurgeTrigger_RequiresGrowthAndTightness()
    {
        // 10 分钟涨 9GB、提交余量 20%、可用 5GB → 触发
        Assert.Equal(ReliefAction.PurgeStandby,
            CommitReliefPolicy.Decide(Input(commitFreePct: 20, growthGb: 9, freeGb: 5)));
        // 可用充足（12GB）→ 不触发
        Assert.Equal(ReliefAction.None,
            CommitReliefPolicy.Decide(Input(commitFreePct: 20, growthGb: 9, freeGb: 12)));
    }
}
