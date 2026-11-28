using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 压缩收益历史（v0.25.0）。
///
/// 背景：2026-11-27 同规模的两轮温和压缩分别释放 1493 MB 与 164 MB（差 9 倍），
/// 因为"工作集大"不代表"可回收多"。这个存储把实测释放率记下来，排序改用它。
/// </summary>
public class TrimFeedbackStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestTmp.Dir, "trim-fb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void UnknownName_UsesDefaultRatio()
    {
        var store = new TrimFeedbackStore(_dir);
        Assert.Equal(TrimFeedbackStore.DefaultRatio, store.RatioOf("never-seen"));
    }

    [Fact]
    public void SingleSample_StillUsesDefault_SoOneOddRunCannotFlipTheOrder()
    {
        var store = new TrimFeedbackStore(_dir);
        store.Observe("weird", 1000, 990);          // 单次 0.99
        Assert.Equal(TrimFeedbackStore.DefaultRatio, store.RatioOf("weird"));
    }

    [Fact]
    public void TwoSamples_UseTheAverage()
    {
        var store = new TrimFeedbackStore(_dir);
        store.Observe("p", 1000, 900);              // 0.9
        store.Observe("p", 1000, 300);              // 0.3
        Assert.Equal(0.6, store.RatioOf("p"), precision: 3);
    }

    [Fact]
    public void RatioIsClampedToUnitInterval()
    {
        var store = new TrimFeedbackStore(_dir);
        store.Observe("q", 1000, 5000);             // freed > before → 1
        store.Observe("q", 1000, 0);                // 0
        Assert.Equal(0.5, store.RatioOf("q"), precision: 3);
    }

    [Fact]
    public void PersistsAcrossReload()
    {
        var store = new TrimFeedbackStore(_dir);
        store.Observe("r", 1000, 800);
        store.Observe("r", 1000, 800);
        store.Flush();

        var reloaded = new TrimFeedbackStore(_dir);   // 模拟重启
        Assert.Equal(0.8, reloaded.RatioOf("r"), precision: 3);
    }

    [Fact]
    public void InvalidInput_IsIgnored_NotRecordedAsZero()
    {
        var store = new TrimFeedbackStore(_dir);
        store.Observe("", 1000, 500);               // 无名字
        store.Observe("s", 0, 500);                 // 压前未知
        Assert.Equal(0, store.Count);
    }
}
