using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 低可用兜底的档位判定（v0.24.0）。
///
/// 背景：真机 18:17 一次压了 152 个进程、回收 4 GB，用户体感却是"清理后变卡"——
/// 压缩把页面换出，下次访问要读回，而且**没有撤销 API**。所以档位必须在动手前判清楚：
/// 危急才全量，其余先温和 20 个。
/// </summary>
public class FloorTierTests
{
    [Theory]
    [InlineData(10.0, 50.0, true, null)]                                  // 健康，不动
    [InlineData(3.0, 50.0, true, null)]                                   // 正好 3 GB：阈值是"小于"才动
    [InlineData(2.9, 50.0, true, ResidentEngine.FloorTier.Mild)]          // 偏紧 → 温和
    [InlineData(2.0, 50.0, true, ResidentEngine.FloorTier.Mild)]
    [InlineData(2.0, 50.0, false, ResidentEngine.FloorTier.Skipped)]      // 用户关了自动兜底 → 只通知
    [InlineData(1.4, 50.0, true, ResidentEngine.FloorTier.Critical)]      // 危急 → 全量
    [InlineData(1.4, 50.0, false, ResidentEngine.FloorTier.Critical)]     // 危急优先于开关：不压会 OOM
    [InlineData(20.0, 9.0, true, ResidentEngine.FloorTier.Mild)]          // 提交告急也触发（物理还充足）
    [InlineData(20.0, 10.0, true, null)]                                  // 正好 10%：不动
    public void ClassifyFloor_MapsToTheRightTier(double availGb, double commitFreePct,
        bool autoEnabled, ResidentEngine.FloorTier? expected)
    {
        Assert.Equal(expected, ResidentEngine.ClassifyFloor(availGb, commitFreePct, autoEnabled));
    }

    [Fact]
    public void CriticalTier_IgnoresTheDisabledSwitch_SoOomIsStillPrevented()
    {
        // 单独一条把"危急 ≥ 开关"钉死：这是本次改动里唯一会违背用户设置的路径，
        // 必须有测试守着，避免以后有人"顺手"把顺序调回开关优先。
        Assert.Equal(ResidentEngine.FloorTier.Critical,
            ResidentEngine.ClassifyFloor(FloorCritical(), 50.0, autoReliefEnabled: false));
    }

    private static double FloorCritical() => ResidentEngine.FloorCriticalGb - 0.1;
}
