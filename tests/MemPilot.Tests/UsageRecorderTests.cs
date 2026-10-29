using System.Text.Json;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

public class UsageRecorderTests : IDisposable
{
    private readonly string _dir;

    public UsageRecorderTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-usage-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SystemMemoryInfo Mem() => new()
    {
        TotalPhysicalBytes = 32L * 1024 * 1024 * 1024,
        AvailablePhysicalBytes = 8L * 1024 * 1024 * 1024,
        TotalCommitLimitBytes = 48L * 1024 * 1024 * 1024,
        TotalCommittedBytes = 16L * 1024 * 1024 * 1024
    };

    private static List<ProcessInfo> Procs() => new()
    {
        new ProcessInfo
        {
            Pid = 1, Name = "bigapp", Category = ProcessCategory.ThirdPartyApp,
            Memory = new MemoryMetrics { PrivateWorkingSetBytes = 512L * 1024 * 1024, CpuPercent = 12.5, ThreadCount = 42 }
        }
    };

    [Fact]
    public void FirstSample_IsWritten_AndReadable()
    {
        var recorder = new UsageRecorder(_dir);
        recorder.RecordSample(Mem(), Procs(), AppMode.Game, 10);
        // first call always writes (throttle starts empty)
        var samples = recorder.ReadWindow(TimeSpan.FromHours(1));
        var sample = Assert.Single(samples);
        Assert.Equal("Game", sample.Mode);
        Assert.Equal(10, sample.Interval);
        Assert.Equal(32L * 1024 * 1024 * 1024, sample.Total);
        Assert.Equal(1, sample.Procs);
        Assert.Equal(42, sample.Threads);
        var top = Assert.Single(sample.Top);
        Assert.Equal("bigapp", top.N);
        Assert.Equal(512L * 1024 * 1024, top.W);
    }

    [Fact]
    public void Samples_WithinThrottleWindow_AreSkipped()
    {
        var recorder = new UsageRecorder(_dir);
        recorder.RecordSample(Mem(), Procs(), AppMode.Game, 10);
        recorder.RecordSample(Mem(), Procs(), AppMode.Game, 10); // < 5 min later: throttled
        Assert.Single(recorder.ReadWindow(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void OldSampleFiles_ArePrunedByRetention()
    {
        // simulate an old file (name carries the day) beyond the retention window
        Directory.CreateDirectory(Path.Combine(_dir, "history"));
        var oldPath = Path.Combine(_dir, "history", "samples-20200101.jsonl");
        File.WriteAllText(oldPath, "{}");

        new UsageRecorder(_dir, retentionDays: 30); // ctor prunes

        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public void RecentFiles_SurvivePrune()
    {
        var recorder = new UsageRecorder(_dir);
        recorder.RecordSample(Mem(), Procs(), AppMode.Resident, 10);
        new UsageRecorder(_dir, retentionDays: 30); // re-init prunes nothing recent
        Assert.Single(recorder.ReadWindow(TimeSpan.FromHours(1)));
    }

    // 2026-10-29：文件必须按本地日期命名——UTC 命名会把 0–8 点的样本切进前一天文件，
    // 按天统计的习惯画像会错位。
    [Fact]
    public void SampleFile_UsesLocalDateName()
    {
        var recorder = new UsageRecorder(_dir);
        recorder.RecordSample(Mem(), Procs(), AppMode.Resident, 10);
        var expected = Path.Combine(_dir, "history", $"samples-{DateTime.Now:yyyyMMdd}.jsonl");
        Assert.True(File.Exists(expected), $"expected local-date file at {expected}");
    }

    // 睡眠/重启后的首条采样必须带 gap 语义，否则断档无法与"数据丢失"区分。
    [Fact]
    public void FirstSampleAfterLongGap_IsMarkedResume()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "history"));
        var path = Path.Combine(_dir, "history", $"samples-{DateTime.Now:yyyyMMdd}.jsonl");
        var old = new UsageSample { T = DateTime.UtcNow.AddHours(-6), Total = 1, Avail = 1, Commit = 1, CommitLimit = 1, Procs = 1, Threads = 1 };
        File.WriteAllText(path, JsonSerializer.Serialize(old) + Environment.NewLine);

        var recorder = new UsageRecorder(_dir);
        recorder.RecordSample(Mem(), Procs(), AppMode.Resident, 10);

        var samples = recorder.ReadWindow(TimeSpan.FromHours(24));
        var last = samples[^1];
        Assert.True(last.GapMinutes >= 350, $"gap minutes: {last.GapMinutes}");
        Assert.Equal("resume", last.Event);
    }
}
