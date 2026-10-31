using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 影子模式结算（棱镜 completeness/shadow-loop）：影子此前只写不读，无法算出准确率。
/// 这里守住三件事：① 影子/自动/人工三类事件分别计数；② 只有 ±10 分钟内的人工切换才算标签，
/// 其余记「未验证」而不是当作正确；③ 无标签时不编造准确率，只报频率。
/// </summary>
public class SceneShadowReportTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);

    private static DecisionEntry Shadow(DateTime at, string target) => new()
    { TimestampUtc = at, Kind = "SceneShadow", Target = target, Decision = "如果自动切换：会切", Reason = "test" };

    private static DecisionEntry AutoSwitch(DateTime at, string target) => new()
    { TimestampUtc = at, Kind = "ModeSwitch", Target = target, Decision = "切换", Reason = "auto" };

    private static AuditEntry Manual(DateTime at, string from, string to) => new()
    { TimestampUtc = at, Action = "ModeSwitch", Target = $"{from} → {to}", Level = "Manual" };

    [Fact]
    public void EmptyData_ReportsZero_AndSaysThresholdMayBeHigh()
    {
        var r = SceneShadowReport.Build(Array.Empty<DecisionEntry>(), Array.Empty<AuditEntry>(), Now);
        Assert.Equal(0, r.ShadowTotal);
        Assert.Equal(0, r.Agree);
        Assert.Equal(0, r.Disagree);
        Assert.Contains("没有", r.Text);
        Assert.Contains("阈值", r.Text);
    }

    [Fact]
    public void ManualSwitchSameTarget_WithinWindow_CountsAsAgree()
    {
        var decisions = new[] { Shadow(Now.AddMinutes(-5), "Game") };
        var audits = new[] { Manual(Now, "Resident", "Game") };
        var r = SceneShadowReport.Build(decisions, audits, Now);

        Assert.Equal(1, r.ShadowTotal);
        Assert.Equal(1, r.ManualSwitchTotal);
        Assert.Equal(1, r.Agree);
        Assert.Equal(0, r.Disagree);
        Assert.Equal(0, r.Unverified);
        Assert.Contains("100%", r.Text);
    }

    [Fact]
    public void ManualSwitchOtherTarget_CountsAsDisagree()
    {
        var decisions = new[] { Shadow(Now.AddMinutes(-3), "Game") };
        var audits = new[] { Manual(Now, "Resident", "Dev") };
        var r = SceneShadowReport.Build(decisions, audits, Now);

        Assert.Equal(0, r.Agree);
        Assert.Equal(1, r.Disagree);
        Assert.Contains("0%", r.Text);
    }

    [Fact]
    public void ManualSwitchOutsideWindow_IsUnverified_NotAgree()
    {
        var decisions = new[] { Shadow(Now.AddMinutes(-45), "Ai") };
        var audits = new[] { Manual(Now, "Resident", "Ai") };
        var r = SceneShadowReport.Build(decisions, audits, Now);

        Assert.Equal(0, r.Agree);
        Assert.Equal(0, r.Disagree);
        Assert.Equal(1, r.Unverified);
        Assert.Contains("可对照 0 次", r.Text); // 不得把「附近没人操作」当成判对
    }

    [Fact]
    public void AutoSwitches_CountedSeparatelyFromShadow()
    {
        var decisions = new[]
        {
            Shadow(Now.AddHours(-2), "Game"),
            AutoSwitch(Now.AddHours(-1), "Game"),
        };
        var r = SceneShadowReport.Build(decisions, Array.Empty<AuditEntry>(), Now);
        Assert.Equal(1, r.ShadowTotal);
        Assert.Equal(1, r.AutoSwitchTotal);
        Assert.Equal(0, r.ManualSwitchTotal);
    }

    [Fact]
    public void DailyBuckets_SplitAcrossLocalDays()
    {
        var decisions = new[]
        {
            Shadow(Now.AddDays(-1), "Game"),
            Shadow(Now, "Ai"),
            Shadow(Now.AddMinutes(-2), "Ai"),
        };
        var r = SceneShadowReport.Build(decisions, Array.Empty<AuditEntry>(), Now);

        Assert.Equal(7, r.Daily.Count);
        Assert.Equal(1, r.Daily[^2].ShadowCount);   // 昨天
        Assert.Equal(2, r.Daily[^1].ShadowCount);   // 今天
        Assert.Equal(2, r.Daily[^1].Targets["Ai"]);
        Assert.Equal(0, r.Daily[0].ShadowCount);
    }

    [Fact]
    public void HighFrequency_WithoutLabels_FlagsOversensitivity()
    {
        var decisions = Enumerable.Range(0, 100)
            .Select(i => Shadow(Now.AddMinutes(-i * 30), "Game"))
            .ToList();
        var r = SceneShadowReport.Build(decisions, Array.Empty<AuditEntry>(), Now);
        Assert.True(r.ShadowTotal > 80);
        Assert.Contains("偏", r.Text);
    }
}
