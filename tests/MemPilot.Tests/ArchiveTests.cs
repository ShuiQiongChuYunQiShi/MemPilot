using System.IO;
using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.18.0 审计冷热分层：热窗口按条数淘汰，被淘汰的条目必须进 archive/，
/// 否则"每个自动决策都可审计"在重度使用两三周后就不成立。
/// </summary>
public class ArchiveTests : IDisposable
{
    private readonly string _dir;

    public ArchiveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mempilot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ArchivePath(string prefix)
        => Path.Combine(_dir, "archive", $"{prefix}-{DateTime.Now:yyyyMM}.jsonl");

    [Fact]
    public void DecisionLog_Eviction_ArchivesOldEntries()
    {
        var log = new DecisionLog(_dir);
        for (int i = 0; i < 5100; i++)
            log.Log(new DecisionEntry { Kind = "GrowthWatch", Target = i.ToString(), Decision = "提示", Reason = "x" });
        log.Flush();

        Assert.True(File.Exists(ArchivePath("decisions")), "被淘汰的决策记录应写入归档");
        var lines = File.ReadAllLines(ArchivePath("decisions")).Count(l => l.Length > 0);
        Assert.InRange(lines, 100, 200);
        Assert.Equal(5000, log.CountByKind().Values.Sum());   // 热窗口仍守 5000 上限
    }

    [Fact]
    public void AuditLogger_Eviction_ArchivesOldEntries()
    {
        var audit = new AuditLogger(_dir);
        for (int i = 0; i < 2100; i++)
            audit.Log(new AuditEntry { Action = "Test", Target = i.ToString(), Level = "L1", Result = "ok" });
        audit.Flush();

        Assert.True(File.Exists(ArchivePath("audit")), "被淘汰的审计记录应写入归档");
        var lines = File.ReadAllLines(ArchivePath("audit")).Count(l => l.Length > 0);
        Assert.InRange(lines, 100, 200);
        Assert.Equal(2000, audit.Count);
    }

    [Fact]
    public void ArchiveWriter_Prune_RemovesExpiredMonths()
    {
        var archiveDir = Path.Combine(_dir, "archive");
        Directory.CreateDirectory(archiveDir);
        var old = Path.Combine(archiveDir, "audit-202001.jsonl");
        var recent = Path.Combine(archiveDir, $"audit-{DateTime.Now:yyyyMM}.jsonl");
        File.WriteAllText(old, "{}\n");
        File.WriteAllText(recent, "{}\n");

        var writer = new ArchiveWriter(_dir, retentionMonths: 12);
        writer.Prune(12);

        Assert.False(File.Exists(old), "超过保留期的归档应被删除");
        Assert.True(File.Exists(recent), "保留期内的归档必须保留");
    }
}

/// <summary>v0.18.0 双频调度的频率裁决（纯函数）。</summary>
public class TickSchedulerTests
{
    [Fact]
    public void Pressure_RunsAtProbeCadence()
    {
        Assert.False(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(4), false, true, 10));
        Assert.True(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(5), false, true, 10));
    }

    [Fact]
    public void UiActive_UsesConfiguredInterval()
    {
        Assert.False(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(9), true, false, 10));
        Assert.True(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(10), true, false, 10));
    }

    [Fact]
    public void UiHidden_NoPressure_BacksOffToAtLeast60s()
    {
        Assert.False(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(59), false, false, 10));
        Assert.True(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(60), false, false, 10));
        // 配置间隔 30s → 空闲按 3 倍 = 90s
        Assert.False(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(89), false, false, 30));
        Assert.True(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(90), false, false, 30));
    }

    [Fact]
    public void IntervalClampsToAtLeast3s()
    {
        Assert.True(ResidentEngine.ShouldFullRefresh(TimeSpan.FromSeconds(3), true, false, 0));
    }

    [Fact]
    public void IsPressure_Boundaries()
    {
        const long GB = 1024L * 1024 * 1024;
        var mem = new SystemMemoryInfo
        {
            AvailablePhysicalBytes = 8 * GB,
            TotalCommitLimitBytes = 40 * GB,
            TotalCommittedBytes = 20 * GB
        };
        Assert.False(ResidentEngine.IsPressure(mem));

        mem.AvailablePhysicalBytes = 3 * GB;      // 阈值是严格小于
        Assert.False(ResidentEngine.IsPressure(mem));
        mem.AvailablePhysicalBytes = 2 * GB;
        Assert.True(ResidentEngine.IsPressure(mem));

        mem.AvailablePhysicalBytes = 8 * GB;
        mem.TotalCommittedBytes = 36 * GB;        // 提交余量正好 10%
        Assert.False(ResidentEngine.IsPressure(mem));
        mem.TotalCommittedBytes = 37 * GB;        // 7.5%
        Assert.True(ResidentEngine.IsPressure(mem));
    }
}
