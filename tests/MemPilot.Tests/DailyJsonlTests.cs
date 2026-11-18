using System.IO;
using MemPilot.Helpers;
using Xunit;

namespace MemPilot.Tests;

public sealed class DailyJsonlTests : IDisposable
{
    private readonly string _tempDir;

    private record TestItem(int Id, string Name);

    public DailyJsonlTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mempilot-dailyjsonl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void PathFor_FormatsDateAndPrefixCorrectly()
    {
        var date = new DateTime(2026, 11, 18);
        var path = DailyJsonl.PathFor(_tempDir, "self", date);

        Assert.Equal(Path.Combine(_tempDir, "self-20261118.jsonl"), path);
    }

    [Fact]
    public void Prune_KeepsTodayAndDeletesFilesOlderThanRetention()
    {
        var today = DateTime.Now.Date;
        var todayFile = DailyJsonl.PathFor(_tempDir, "self", today);
        var withinRetentionFile = DailyJsonl.PathFor(_tempDir, "self", today.AddDays(-5));
        var expiredFile = DailyJsonl.PathFor(_tempDir, "self", today.AddDays(-6));
        var otherPrefixFile = DailyJsonl.PathFor(_tempDir, "other", today.AddDays(-10));

        File.WriteAllText(todayFile, "{}");
        File.WriteAllText(withinRetentionFile, "{}");
        File.WriteAllText(expiredFile, "{}");
        File.WriteAllText(otherPrefixFile, "{}");

        int removed = DailyJsonl.Prune(_tempDir, "self", retentionDays: 5);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(todayFile));
        Assert.True(File.Exists(withinRetentionFile));
        Assert.False(File.Exists(expiredFile));
        Assert.True(File.Exists(otherPrefixFile));
    }

    [Fact]
    public void ReadRecent_SkipsCorruptLinesWithoutThrowing()
    {
        var todayFile = DailyJsonl.PathFor(_tempDir, "self", DateTime.Now);
        DailyJsonl.Append(todayFile, new TestItem(1, "first"));
        DailyJsonl.Append(todayFile, "{ this is corrupt json ");
        DailyJsonl.Append(todayFile, "   ");
        DailyJsonl.Append(todayFile, new TestItem(2, "second"));

        var items = DailyJsonl.ReadRecent<TestItem>(_tempDir, "self", days: 1).ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Id);
        Assert.Equal(2, items[1].Id);
    }
}
