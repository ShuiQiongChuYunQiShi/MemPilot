using System.IO;
using System.Reflection;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

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
            recorder.RecordTick(i * 10, stageFailuresTotal: 0);

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
        recorder.RecordTick(50, stageFailuresTotal: 1);

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
        recorder1.RecordTick(42, stageFailuresTotal: 0);

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
        recorder.RecordTick(15, stageFailuresTotal: 0);

        // Trigger minute rollover while keeping last CPU sample < 1 second ago
        SetPrivateDateTime(recorder, "_windowStartUtc", DateTime.UtcNow.AddMinutes(-1));
        SetPrivateDateTime(recorder, "_lastCpuSampleUtc", DateTime.UtcNow);
        recorder.Heartbeat(stageFailuresTotal: 0);

        var sample = recorder.Latest();
        Assert.NotNull(sample);
        Assert.Equal(0, sample!.Cpu);
    }
}
