using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.20.0 决策日志噪声折叠：默认隐藏「建议/增长观察」。
/// 关键约束：JunkAdvice 这个 Kind 里混着用户的挂起/忽略与到期自动恢复——
/// 按 Kind 一刀切会让用户以为挂起没生效，所以判定必须看 (Kind, Decision) 组合。
/// </summary>
public class DecisionLogFilterTests
{
    private static DecisionEntry E(string kind, string decision = "提示")
        => new() { Kind = kind, Decision = decision, Target = "x", Reason = "r" };

    [Theory]
    [InlineData("JunkAdvice", "建议", true)]      // 引擎建议：噪声
    [InlineData("JunkAdvice", "无候选", true)]     // 立即检查无果：噪声
    [InlineData("JunkAdvice", "跳过", true)]       // v0.17.0 历史数据：噪声
    [InlineData("JunkAdvice", "挂起", false)]      // 用户动作：必须显示
    [InlineData("JunkAdvice", "挂起失败", false)]
    [InlineData("JunkAdvice", "忽略", false)]
    [InlineData("JunkAdvice", "自动恢复", false)]   // 到期自动恢复：必须显示
    [InlineData("JunkAdvice", "恢复重试", false)]
    [InlineData("JunkAdvice", "恢复取消", false)]
    [InlineData("GrowthWatch", "提示", true)]      // 增长观察：噪声
    [InlineData("AutoCleanup", "执行 L1 回收工作集", false)]
    [InlineData("CommitRelief", "执行", false)]
    [InlineData("", "x", false)]
    public void IsNoise_ClassifiesByKindAndDecision(string kind, string decision, bool noise)
        => Assert.Equal(noise, DecisionLogFilter.IsNoise(E(kind, decision)));

    [Fact]
    public void Filter_HidesNoiseButKeepsRealActions()
    {
        var list = new List<DecisionEntry>
        {
            E("JunkAdvice", "建议"),
            E("JunkAdvice", "挂起"),
            E("GrowthWatch"),
            E("AutoCleanup", "执行 L1 回收工作集"),
        };
        Assert.Equal(2, DecisionLogFilter.Filter(list, showNoise: false).Count());
        Assert.Equal(4, DecisionLogFilter.Filter(list, showNoise: true).Count());
    }

    [Fact]
    public void Filter_KindAndNoiseAreCombinedWithAnd()
    {
        var list = new List<DecisionEntry>
        {
            E("JunkAdvice", "建议"),
            E("JunkAdvice", "挂起"),
            E("GrowthWatch"),
            E("AutoCleanup", "执行 L1 回收工作集"),
        };

        // Kind=JunkAdvice + 折叠噪声 → 只剩「挂起」（真实动作）
        Assert.Single(DecisionLogFilter.Filter(list, showNoise: false, kind: "JunkAdvice"));
        // 打开噪声 → 「建议」+「挂起」
        Assert.Equal(2, DecisionLogFilter.Filter(list, showNoise: true, kind: "JunkAdvice").Count());
        // 其它 Kind 过滤
        Assert.Single(DecisionLogFilter.Filter(list, showNoise: true, kind: "GrowthWatch"));
        Assert.Single(DecisionLogFilter.Filter(list, showNoise: false, kind: "AutoCleanup"));
        // 空串 = 全部 Kind（仍折叠噪声）
        Assert.Equal(2, DecisionLogFilter.Filter(list, showNoise: false, kind: DecisionLogFilter.AllKinds).Count());
        Assert.Equal(4, DecisionLogFilter.Filter(list, showNoise: true, kind: null).Count());
    }

    [Fact]
    public void Filter_KindMatchIsCaseInsensitive()
        => Assert.Single(DecisionLogFilter.Filter(new[] { E("autocleanup", "执行") }, false, "AutoCleanup"));

    [Fact]
    public void KindMatch_IsCaseInsensitive()
        => Assert.True(DecisionLogFilter.IsNoise(E("junkadvice", "建议")));

    [Fact]
    public void NullEntry_IsNotNoise()
        => Assert.False(DecisionLogFilter.IsNoise(null));
}
