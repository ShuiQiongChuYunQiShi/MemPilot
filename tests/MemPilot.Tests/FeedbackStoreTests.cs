using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 反馈库（v0.3.7）：反馈是本地分类修正的唯一入口，标记错了必须能撤销；
/// 空表单（无进程名/路径）不允许产生"永远匹配不到"的垃圾记录。
/// </summary>
public class FeedbackStoreTests : IDisposable
{
    private readonly string _dir;

    public FeedbackStoreTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-fb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Remove_DeletesAndPersists()
    {
        var store = new FeedbackStore(_dir);
        var e = new FeedbackEntry { ProcessName = "wegame.exe", MarkKeep = true };
        store.Add(e);
        Assert.Single(store.All());

        Assert.True(store.Remove(e.Id));
        Assert.Empty(store.All());

        var reloaded = new FeedbackStore(_dir); // 重新加载：删除必须落盘
        Assert.Empty(reloaded.All());
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse()
    {
        var store = new FeedbackStore(_dir);
        Assert.False(store.Remove(Guid.NewGuid()));
    }

    [Fact]
    public void EmptyEntry_NeverMatchesAnyProcess()
    {
        var store = new FeedbackStore(_dir);
        store.Add(new FeedbackEntry()); // 空表单：无名称无路径
        Assert.Null(store.Find("anything", @"C:\x\y.exe", "pub"));
        Assert.Null(store.Find("", "", ""));
    }

    [Fact]
    public void KeepMark_MakesProcessWhitelisted_AndCleanMarkBlacklisted()
    {
        var dialog = Path.Combine(TestTmp.Dir, "mt-tests-catcls-fb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dialog);
        try
        {
            var store = new FeedbackStore(dialog);
            var keep = new FeedbackEntry { ProcessName = "keepme.exe", MarkKeep = true, ReportedCategory = ProcessCategory.ThirdPartyApp };
            var clean = new FeedbackEntry { ProcessName = "cleanme.exe", MarkCleanable = true, ReportedCategory = ProcessCategory.Bloatware };
            store.Add(keep);
            store.Add(clean);

            var engine = new ClassificationEngine(store);
            var p1 = new ProcessInfo { Pid = 1, Name = "keepme.exe", ExecutablePath = @"C:\a\keepme.exe" };
            var p2 = new ProcessInfo { Pid = 2, Name = "cleanme.exe", ExecutablePath = @"C:\a\cleanme.exe" };
            engine.Classify(p1, new(), new HashSet<string>());
            engine.Classify(p2, new(), new HashSet<string>());

            Assert.True(p1.IsWhitelisted);
            Assert.Equal("user_feedback", p1.CategoryReason);
            Assert.True(p2.IsBlacklisted);
            Assert.Equal(ProcessCategory.Bloatware, p2.Category);
        }
        finally { try { Directory.Delete(dialog, true); } catch { } }
    }

    [Fact]
    public void DisplayFields_AreLocalTimeAndChineseScope()
    {
        var e = new FeedbackEntry { TimestampUtc = new DateTime(2026, 11, 1, 4, 14, 30, DateTimeKind.Utc), Scope = "software", MarkSystem = true };
        // 不能写死时区：CI runner 是 UTC，开发机是 UTC+8（v0.3.8 首次发布就因此红了）。
        // 断言形状 + 与 ToLocalTime 的换算关系，两边都成立。
        Assert.Matches(@"^\d{2}-\d{2} \d{2}:\d{2}$", e.TimeText);
        Assert.Equal(e.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm"), e.TimeText);
        Assert.Equal("软件", e.ScopeText);
        Assert.Contains("系统", e.EffectText);
    }
}
