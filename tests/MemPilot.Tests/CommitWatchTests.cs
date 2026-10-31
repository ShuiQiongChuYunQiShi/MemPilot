using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>提交内存告警判定：增长本身不是问题，吃光余量才是。</summary>
public class CommitWatchTests
{
    // 2026-10-31 07:42 误报现场：开机自启风暴（提交 5 分钟 +18.6GB、进程 224→631），
    // 但可用内存 14.1GB 完全健康 —— 这种增长不该报"内存告急"。
    [Fact]
    public void BootStorm_WithHealthyHeadroom_IsNotAnAlert()
    {
        var level = ResidentEngine.EvaluateCommitWatch(growthGb: 14.8, freeGb: 14.1, commitFreeGb: 21.1, commitLimitGb: 67);
        Assert.Equal(ResidentEngine.CommitWatchLevel.None, level);
    }

    // 增长 + 真的在吃余量（可用 5GB、提交余量 15/67）才是"吃紧"
    [Fact]
    public void GenuineTightSurge_AlertsAsTight()
    {
        var level = ResidentEngine.EvaluateCommitWatch(growthGb: 10, freeGb: 5, commitFreeGb: 15, commitLimitGb: 67);
        Assert.Equal(ResidentEngine.CommitWatchLevel.Tight, level);
    }

    // 提交余量跌破 10% 是硬信号，与增长无关
    [Fact]
    public void ExhaustedCommit_IsCritical()
    {
        var level = ResidentEngine.EvaluateCommitWatch(growthGb: 0, freeGb: 8, commitFreeGb: 4, commitLimitGb: 67);
        Assert.Equal(ResidentEngine.CommitWatchLevel.Critical, level);
    }

    [Fact]
    public void ShrinkingCommit_NoAlert()
    {
        Assert.Equal(ResidentEngine.CommitWatchLevel.None,
            ResidentEngine.EvaluateCommitWatch(growthGb: -5, freeGb: 20, commitFreeGb: 30, commitLimitGb: 67));
    }

    [Fact]
    public void ZeroLimit_NoAlert()
    {
        Assert.Equal(ResidentEngine.CommitWatchLevel.None,
            ResidentEngine.EvaluateCommitWatch(growthGb: 20, freeGb: 1, commitFreeGb: 1, commitLimitGb: 0));
    }
}
