using MemPilot.App.Cli;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// `mempilot lease run` 的参数解析（v0.25.0）。
/// 只测解析——真正的 acquire/子进程托管要靠真机端到端（HTTP + Job Object 都不适合单测）。
/// 但解析错了会让 CLI 静默起错命令，所以边界值必须在测试里钉住。
/// </summary>
public class LeaseRunOptionTests
{
    [Fact]
    public void ParsesTargetAndCommand()
    {
        var o = LeaseRun.Options.Parse(new[] { "--gb", "16", "--", "python", "gen.py" }, out var err);

        Assert.NotNull(o);
        Assert.Null(err);
        Assert.Equal(16, o!.Gb);
        Assert.Equal(new[] { "python", "gen.py" }, o.Command);
        Assert.Equal("soft", o.Policy);          // 默认保守
        Assert.Equal(3600, o.Ttl);
        Assert.False(o.DryRun);
    }

    [Fact]
    public void ParsesHardPolicyAndRequester()
    {
        var o = LeaseRun.Options.Parse(
            new[] { "--gb", "24", "--policy", "HARD", "--requester", "claude-code", "--", "x" }, out _);

        Assert.Equal("hard", o!.Policy);
        Assert.Equal("claude-code", o.Requester);
    }

    [Fact]
    public void UnknownOptionIsRejected()
    {
        Assert.Null(LeaseRun.Options.Parse(new[] { "--gb", "8", "--nope" }, out var err));
        Assert.Contains("unknown option", err);
    }

    [Fact]
    public void MissingValueIsReported()
    {
        Assert.Null(LeaseRun.Options.Parse(new[] { "--gb" }, out var err));
        Assert.Contains("--gb", err);
    }

    [Fact]
    public void WaitAndTtlAreClamped()
    {
        var o = LeaseRun.Options.Parse(
            new[] { "--gb", "1", "--wait", "9999", "--ttl", "1", "--", "x" }, out _);

        Assert.Equal(600, o!.WaitSeconds);       // 最长 10 分钟，避免无限排队
        Assert.Equal(30, o.Ttl);                 // 服务端下限
    }

    [Fact]
    public void DryRunNeedsNoCommand()
    {
        var o = LeaseRun.Options.Parse(new[] { "--gb", "8", "--dry-run" }, out _);

        Assert.True(o!.DryRun);
        Assert.Empty(o.Command);
    }
}
