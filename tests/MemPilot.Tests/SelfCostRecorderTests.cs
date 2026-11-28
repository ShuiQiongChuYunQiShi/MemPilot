using System.IO;
using System.Reflection;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.20.0：自身开销分段耗时（枚举/分类/显存/UI）。
/// 背景：11-15 单次 tick 12.2s、11-21 8.8s，但 self-*.jsonl 只有总耗时，
/// 无法判断卡在枚举、签名验证、PDH 还是 UI marshal。
/// </summary>
public sealed class SelfCostRecorderTests : IDisposable
{
    private readonly string _tempDir;

    public SelfCostRecorderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mempilot-selfcost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void SetPrivateDateTime(SelfCostRecorder recorder, string fieldName, DateTime value)
    {
        var field = typeof(SelfCostRecorder).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(recorder, value);
    }

    [Fact]
    public void Heartbeat_ComputesPercentilesAndMax_OnMinuteRollover()
    {
        var recorder = new SelfCostRecorder(_tempDir);

        // 20 ticks: 10, 20, ..., 200
        for (int i = 1; i <= 20; i++)
            recorder.RecordTick(i * 10, null!, stageFailuresTotal: 0);

        // Simulate that the window started 1 minute ago
        SetPrivateDateTime(recorder, "_windowStartUtc", DateTime.UtcNow.AddMinutes(-1));
        recorder.Heartbeat(stageFailuresTotal: 2);

        var latest = recorder.Latest();
        Assert.NotNull(latest);
        Assert.Equal(20, latest!.N);
        Assert.Equal(100, latest.P50); // 10th element (0.50 * 20 = 10 -> index 9 -> 100)
        Assert.Equal(190, latest.P95); // 19th element (0.95 * 20 = 19 -> index 18 -> 190)
        Assert.Equal(200, latest.Max);
        Assert.Equal(2, latest.Fails);
    }

    [Fact]
    public void Heartbeat_DoesNotFlushWithinSameMinute_AndResetsStateAfterFlush()
    {
        var recorder = new SelfCostRecorder(_tempDir);
        recorder.RecordTick(50, null!, stageFailuresTotal: 1);

        // Explicitly align window start with current UTC minute: should not flush
        SetPrivateDateTime(recorder, "_windowStartUtc", DateTime.UtcNow);
        recorder.Heartbeat(stageFailuresTotal: 1);
        Assert.Null(recorder.Latest());
        Assert.Empty(recorder.Recent(1));

        // Roll over to next minute: flushes first sample
        SetPrivateDateTime(recorder, "_windowStartUtc", DateTime.UtcNow.AddMinutes(-1));
        recorder.Heartbeat(stageFailuresTotal: 1);
        Assert.NotNull(recorder.Latest());
        Assert.Equal(1, recorder.Latest()!.N);
        Assert.Equal(1, recorder.Latest()!.Fails);

        // Next minute with no new ticks: resets N and Fails to 0
        SetPrivateDateTime(recorder, "_windowStartUtc", DateTime.UtcNow.AddMinutes(-1));
        recorder.Heartbeat(stageFailuresTotal: 1);
        Assert.Equal(0, recorder.Latest()!.N);
        Assert.Equal(0, recorder.Latest()!.Fails);
        Assert.Equal(2, recorder.Recent(1).Count);
    }

    [Fact]
    public void Heartbeat_WithNoFullRefreshes_RecordsZeroCount()
    {
        var recorder = new SelfCostRecorder(_tempDir);

        SetPrivateDateTime(recorder, "_windowStartUtc", DateTime.UtcNow.AddMinutes(-1));
        recorder.Heartbeat(stageFailuresTotal: 0);

        var sample = recorder.Latest();
        Assert.NotNull(sample);
        Assert.Equal(0, sample!.N);
        Assert.Equal(0, sample.P50);
        Assert.Equal(0, sample.P95);
        Assert.Equal(0, sample.Max);
    }

    [Fact]
    public void Latest_ReturnsCachedSampleFromDiskOnStartup()
    {
        var recorder1 = new SelfCostRecorder(_tempDir);
        recorder1.RecordTick(42, null!, stageFailuresTotal: 0);

        SetPrivateDateTime(recorder1, "_windowStartUtc", DateTime.UtcNow.AddMinutes(-1));
        recorder1.Heartbeat(stageFailuresTotal: 0);

        // New instance pointing to the same directory should warm Latest() from disk
        var recorder2 = new SelfCostRecorder(_tempDir);
        var warmed = recorder2.Latest();

        Assert.NotNull(warmed);
        Assert.Equal(1, warmed!.N);
        Assert.Equal(42, warmed.Max);
    }

    [Fact]
    public void CpuPercent_IsZero_WhenWallClockDeltaIsLessThanOrEqualToOneSecond()
    {
        var recorder = new SelfCostRecorder(_tempDir);
        recorder.RecordTick(15, null!, stageFailuresTotal: 0);

        // Trigger minute rollover while keeping last CPU sample < 1 second ago
        SetPrivateDateTime(recorder, "_windowStartUtc", DateTime.UtcNow.AddMinutes(-1));
        SetPrivateDateTime(recorder, "_lastCpuSampleUtc", DateTime.UtcNow);
        recorder.Heartbeat(stageFailuresTotal: 0);

        var sample = recorder.Latest();
        Assert.NotNull(sample);
        Assert.Equal(0, sample!.Cpu);
    }

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
        var rec = new SelfCostRecorder(_tempDir);
        rec.RecordTick(1200, new RefreshTimings(800, 300, 80, 20, 30), 0);
        rec.RecordTick(1400, new RefreshTimings(1000, 300, 60, 40, 10), 0);

        var sample = rec.FlushForTests();

        Assert.NotNull(sample);
        Assert.Equal(2, sample!.N);
        Assert.Equal(900, sample.EnumMs);
        Assert.Equal(300, sample.ClassifyMs);
        Assert.Equal(70, sample.GpuMs);
        Assert.Equal(30, sample.UiMs);
        Assert.Equal(20, sample.LookupMs);   // v0.22.0：在线查询分段

        var next = rec.FlushForTests();   // 清窗后不应重复累计
        Assert.Equal(0, next!.N);
        Assert.Equal(0, next.EnumMs);
        Assert.Equal(0, next.UiMs);
        Assert.Equal(0, next.LookupMs);
    }

    [Fact]
    public void Latest_ExposesLastSample()
    {
        var rec = new SelfCostRecorder(_tempDir);
        Assert.Null(rec.Latest());
        rec.RecordTick(500, new RefreshTimings(300, 100, 50, 50), 0);
        var s = rec.FlushForTests();
        Assert.Same(s, rec.Latest());
    }

    [Fact]
    public void RecordTick_NullTimings_FallsBackToZero()
    {
        var rec = new SelfCostRecorder(_tempDir);
        rec.RecordTick(700, null!, 0);
        var sample = rec.FlushForTests();
        Assert.Equal(1, sample!.N);
        Assert.Equal(0, sample.EnumMs);
    }

    [Fact]
    public void Recent_ReadsLegacySamplesWithoutStageFields()
    {
        // 升级兼容：v0.19 的 self-*.jsonl 没有 EnumMs/ClassifyMs/GpuMs/UiMs 字段
        var history = Path.Combine(_tempDir, "history");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, $"self-{DateTime.Now:yyyyMMdd}.jsonl"),
            "{\"T\":\"2026-11-20T10:00:00Z\",\"N\":3,\"P50\":100,\"P95\":200,\"Max\":300,\"Rss\":80,\"Cpu\":1.5,\"Fails\":0}\n");

        var rec = new SelfCostRecorder(_tempDir);
        var s = rec.Recent(2).FirstOrDefault();

        Assert.NotNull(s);
        Assert.Equal(300, s!.Max);
        Assert.Equal(0, s.EnumMs);   // 缺字段 → 0，不抛异常
        Assert.Equal(0, s.UiMs);
    }
}
