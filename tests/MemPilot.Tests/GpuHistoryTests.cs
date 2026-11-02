using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 显存历史聚合（v0.3.4）：缺数据不画假 0、峰值/均值按进程聚合、跳增计数。
/// 历史页与将来的"AI 生成会话复盘"共用这份纯函数，所以断言落在数字上。
/// </summary>
public class GpuHistoryTests
{
    private static GpuSample S(long? used, long total, params (string name, long d, long s)[] procs)
        => new GpuSample
        {
            T = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            Used = used,
            Total = total,
            Src = "pdh-adapter",
            Top = procs.Select(p => new GpuSampleProc { N = p.name, D = p.d, S = p.s }).ToList()
        };

    [Fact]
    public void EmptyHistory_ReportsZeroSamples()
    {
        var r = GpuHistory.Build(Array.Empty<GpuSample>());
        Assert.Equal(0, r.SampleCount);
        Assert.Empty(r.Points);
        Assert.Empty(r.Top);
    }

    [Fact]
    public void SamplesWithoutUsed_DoNotEnterCurve()
    {
        var r = GpuHistory.Build(new[] { S(null, 16L << 30), S(null, 16L << 30) });
        Assert.Equal(2, r.SampleCount);
        Assert.Empty(r.Points);       // 不可用的样本不能画成 0%
        Assert.Null(r.PeakUsed);
    }

    [Fact]
    public void CurveAndPeak_UsePercentAndMax()
    {
        var r = GpuHistory.Build(new[] { S(4L << 30, 16L << 30), S(12L << 30, 16L << 30) });
        Assert.Equal(2, r.Points.Count);
        Assert.Equal(25, r.Points[0].Percent, 1);
        Assert.Equal(75, r.Points[1].Percent, 1);
        Assert.Equal(12L << 30, r.PeakUsed);
        Assert.Equal(12L << 30, r.LastUsed);
        Assert.Equal(16L << 30, r.Total);
    }

    [Fact]
    public void TopProcesses_RankedByPeak_WithAverageAndSeen()
    {
        var r = GpuHistory.Build(new[]
        {
            S(8L << 30, 16L << 30, ("comfyui", 6L << 30, 1L << 30), ("chrome", 1L << 30, 0)),
            S(12L << 30, 16L << 30, ("comfyui", 10L << 30, 2L << 30), ("chrome", 1L << 30, 0)),
        });

        Assert.Equal(2, r.Top.Count);
        Assert.Equal("comfyui", r.Top[0].Name);
        Assert.Equal(10L << 30, r.Top[0].PeakDedicatedBytes);
        Assert.Equal(8L << 30, r.Top[0].AvgDedicatedBytes);
        Assert.Equal(2, r.Top[0].Seen);
        Assert.Equal(2L << 30, r.Top[0].PeakSharedBytes);
        Assert.Equal("chrome", r.Top[1].Name);
    }

    [Fact]
    public void Sessions_AreReplayed_WithSingleDipTolerance()
    {
        const long gib = 1024L * 1024 * 1024;
        var samples = new List<GpuSample>();
        long[] series = { 15, 15, 15, 1, 15, 15, 1, 1, 1 };
        for (int i = 0; i < series.Length; i++)
            samples.Add(new GpuSample
            {
                T = new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc).AddSeconds(30 * i),
                Used = series[i] * gib,
                Total = 16 * gib,
                Top = new List<GpuSampleProc> { new() { N = "python", I = 100 + i, D = series[i] * gib } }
            });

        var r = GpuHistory.Build(samples);
        Assert.Single(r.Sessions);
        Assert.Equal(15 * gib, r.Sessions[0].PeakUsedBytes);
        Assert.Equal("python", r.Sessions[0].DominantName);
    }

    [Fact]
    public void Events_AreCountedSeparately()
    {
        var s1 = S(1L << 30, 16L << 30); s1.Event = "gpu-spike";
        var s2 = S(2L << 30, 16L << 30); s2.Event = "resume";
        var r = GpuHistory.Build(new[] { s1, s2 });
        Assert.Equal(1, r.SpikeCount);
        Assert.Equal(1, r.ResumeCount);
    }
}
