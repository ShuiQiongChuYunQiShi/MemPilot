using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 显存落盘（v0.3.4）：单独文件、按本地日切分、跳增/醒来标记、保留清理、读回。
/// 这些是将来 AI 决策的数据来源，格式坏了就等于历史断了，所以逐字段断言。
/// </summary>
public class GpuRecorderTests : IDisposable
{
    private readonly string _dir;

    public GpuRecorderTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-gpurec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static GpuMemorySnapshot Snap(long used, long procBytes = 1024L * 1024 * 100)
    {
        var s = new GpuMemorySnapshot { AdapterAvailable = true, AdapterDedicatedBytes = used };
        if (procBytes > 0)
        {
            s.ProcessDedicatedAvailable = true;
            s.ProcessDedicated[10] = procBytes;
            s.ProcessShared[10] = procBytes / 4;
        }
        return s;
    }

    private static List<ProcessInfo> Procs() => new() { new ProcessInfo { Pid = 10, Name = "python" } };
    private static List<GpuAdapterInfo> Adapters() => new() { new GpuAdapterInfo { Name = "GPU", DedicatedVideoMemoryBytes = 16L * 1024 * 1024 * 1024 } };

    [Fact]
    public void Write_CreatesSampleFile_AndReadWindowRoundTrips()
    {
        var rec = new GpuRecorder(_dir, 30);
        var now = DateTime.UtcNow;
        var sample = rec.Write(Snap(8L << 30), Procs(), Adapters(), null, 30, now);

        Assert.Equal(8L << 30, sample.Used);
        Assert.Equal("pdh-adapter", sample.Src);
        Assert.Equal(16L << 30, sample.Total);
        Assert.Single(sample.Top);
        Assert.Equal("python", sample.Top[0].N);
        Assert.Equal(10, sample.Top[0].I);

        var files = Directory.GetFiles(Path.Combine(_dir, "history"), "gpu-*.jsonl");
        Assert.True(files.Length == 1, $"expect 1 file, got {files.Length}");
        Assert.Contains("python", File.ReadAllText(files[0]));

        var back = rec.ReadWindow(TimeSpan.FromHours(1));
        Assert.Single(back);
        Assert.Equal(8L << 30, back[0].Used);
        Assert.Equal(50, (int)back[0].UsedPercent!.Value); // 8/16 GiB
    }

    [Fact]
    public void JumpOfFourGigabytes_IsMarkedAsSpike()
    {
        var rec = new GpuRecorder(_dir, 30);
        var t0 = DateTime.UtcNow.AddMinutes(-1);
        rec.Write(Snap(1L << 30), Procs(), Adapters(), null, 30, t0);
        var s = rec.Write(Snap(6L << 30), Procs(), Adapters(), null, 30, t0.AddSeconds(30));
        Assert.Equal("gpu-spike", s.Event);
    }

    [Fact]
    public void LongGap_IsMarkedAsResume_NotSpike()
    {
        var rec = new GpuRecorder(_dir, 30);
        var t0 = DateTime.UtcNow.AddHours(-1);
        rec.Write(Snap(1L << 30), Procs(), Adapters(), null, 30, t0);
        var s = rec.Write(Snap(6L << 30), Procs(), Adapters(), null, 30, t0.AddMinutes(10));
        Assert.Equal("resume", s.Event);
    }

    [Fact]
    public void UnavailableSnapshot_RecordsNullUsed_NotZero()
    {
        var rec = new GpuRecorder(_dir, 30);
        var s = rec.Write(new GpuMemorySnapshot(), Procs(), Adapters(), null, 30, DateTime.UtcNow);
        Assert.Null(s.Used);
        Assert.Equal("none", s.Src);
        Assert.Null(s.UsedPercent);
    }

    [Fact]
    public void Prune_DeletesFilesOlderThanRetention()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "history"));
        var old = Path.Combine(_dir, "history", "gpu-20200101.jsonl");
        File.WriteAllText(old, "{}\n");
        _ = new GpuRecorder(_dir, 1);
        Assert.False(File.Exists(old));
    }
}
