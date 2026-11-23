using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.20.0：自身开销分段耗时（枚举/分类/显存/UI）。
/// 背景：11-15 单次 tick 12.2s、11-21 8.8s，但 self-*.jsonl 只有总耗时，
/// 无法判断卡在枚举、签名验证、PDH 还是 UI marshal。
/// </summary>
public class SelfCostRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestTmp.Dir, "self-cost-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void AverageOf_ComputesPerStage_AndHandlesEmpty()
    {
        var list = new List<RefreshTimings>
        {
            new(100, 50, 10, 5),
            new(300, 150, 30, 15),
            new(200, 100, 20, 10),
        };
        Assert.Equal(200, SelfCostRecorder.AverageOf(list, t => t.EnumMs));
        Assert.Equal(100, SelfCostRecorder.AverageOf(list, t => t.ClassifyMs));
        Assert.Equal(20, SelfCostRecorder.AverageOf(list, t => t.GpuMs));
        Assert.Equal(10, SelfCostRecorder.AverageOf(list, t => t.UiMs));
        Assert.Equal(0, SelfCostRecorder.AverageOf(new List<RefreshTimings>(), t => t.EnumMs));
    }

    [Fact]
    public void Flush_WritesStageAverages_AndClearsWindow()
    {
        var rec = new SelfCostRecorder(_dir);
        rec.RecordTick(1200, new RefreshTimings(800, 300, 80, 20), 0);
        rec.RecordTick(1400, new RefreshTimings(1000, 300, 60, 40), 0);

        var sample = rec.FlushForTests();

        Assert.NotNull(sample);
        Assert.Equal(2, sample!.N);
        Assert.Equal(900, sample.EnumMs);
        Assert.Equal(300, sample.ClassifyMs);
        Assert.Equal(70, sample.GpuMs);
        Assert.Equal(30, sample.UiMs);

        var next = rec.FlushForTests();   // 清窗后不应重复累计
        Assert.Equal(0, next!.N);
        Assert.Equal(0, next.EnumMs);
        Assert.Equal(0, next.UiMs);
    }

    [Fact]
    public void Latest_ExposesLastSample()
    {
        var rec = new SelfCostRecorder(_dir);
        Assert.Null(rec.Latest());
        rec.RecordTick(500, new RefreshTimings(300, 100, 50, 50), 0);
        var s = rec.FlushForTests();
        Assert.Same(s, rec.Latest());
    }

    [Fact]
    public void RecordTick_NullTimings_FallsBackToZero()
    {
        var rec = new SelfCostRecorder(_dir);
        rec.RecordTick(700, null!, 0);
        var sample = rec.FlushForTests();
        Assert.Equal(1, sample!.N);
        Assert.Equal(0, sample.EnumMs);
    }

    [Fact]
    public void Recent_ReadsLegacySamplesWithoutStageFields()
    {
        // 升级兼容：v0.19 的 self-*.jsonl 没有 EnumMs/ClassifyMs/GpuMs/UiMs 字段
        var history = Path.Combine(_dir, "history");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, $"self-{DateTime.Now:yyyyMMdd}.jsonl"),
            "{\"T\":\"2026-11-20T10:00:00Z\",\"N\":3,\"P50\":100,\"P95\":200,\"Max\":300,\"Rss\":80,\"Cpu\":1.5,\"Fails\":0}\n");

        var rec = new SelfCostRecorder(_dir);
        var s = rec.Recent(2).FirstOrDefault();

        Assert.NotNull(s);
        Assert.Equal(300, s!.Max);
        Assert.Equal(0, s.EnumMs);   // 缺字段 → 0，不抛异常
        Assert.Equal(0, s.UiMs);
    }
}
