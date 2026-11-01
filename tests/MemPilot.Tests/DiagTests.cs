using MemPilot.Helpers;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 统一故障诊断（v0.3.9，棱镜 backend/error-handling）：采集类失败必须可查，
/// 但记录要有上限、要按区域聚合，不能把内存刷爆或让界面只看到一个巨大的计数。
/// </summary>
/// <summary>Diag 是全局静态状态：本集合内的测试串行执行，避免并行计数互相污染。</summary>
[CollectionDefinition("diag-global")]
public class DiagCollection { }

[Collection("diag-global")]
public class DiagTests : IDisposable
{
    public DiagTests() { Diag.Reset(); Diag.LogPath = null; }

    public void Dispose() { Diag.Reset(); Diag.LogPath = null; }

    [Fact]
    public void StartsClean()
    {
        Assert.Equal(0, Diag.Total);
        Assert.Contains("无失败记录", Diag.Summary());
        Assert.Empty(Diag.Recent());
    }

    [Fact]
    public void AggregatesByArea()
    {
        Diag.Record("catalog:empty", "三个来源均为空");
        Diag.Record("catalog:empty", "三个来源均为空");
        Diag.Record("proc-enum", "12/380 个进程读取受限");

        Assert.Equal(3, Diag.Total);
        var summary = Diag.Summary();
        Assert.Contains("catalog:empty ×2", summary);
        Assert.Contains("proc-enum", summary);
        Assert.NotNull(Diag.LastFailureUtc);
    }

    [Fact]
    public void StoresExceptionTypeAndMessage()
    {
        Diag.Record("scene", new InvalidOperationException("boom"));
        var latest = Diag.Recent()[0];
        Assert.Contains("scene", latest);
        Assert.Contains("InvalidOperationException", latest);
        Assert.Contains("boom", latest);
    }

    [Fact]
    public void RecentIsCappedAndNewestFirst()
    {
        for (int i = 0; i < 25; i++) Diag.Record("area" + i, "detail" + i);
        var recent = Diag.Recent();
        Assert.Equal(20, recent.Count);           // 环形上限
        Assert.Contains("area24", recent[0]);     // 最新在最前
        Assert.DoesNotContain(recent, l => l.Contains("area0 "));
    }

    [Fact]
    public void WritesToLogFile_WhenConfigured()
    {
        var dir = Path.Combine(TestTmp.Dir, "mt-tests-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Diag.LogPath = Path.Combine(dir, "diag.log");
            Diag.Record("gpu-mem", "counters unavailable");
            var text = File.ReadAllText(Diag.LogPath);
            Assert.Contains("gpu-mem", text);
        }
        finally
        {
            Diag.LogPath = null;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
