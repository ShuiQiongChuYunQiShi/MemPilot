using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 页签重建策略（棱镜 test/ui-regression）：v0.1.6 修过「切页签卡 4-5 秒」，
/// 但这段判断埋在 VM 的 switch 里、没有测试守着，重构时可能悄悄退回「每 tick 全量重建」。
/// 这里两层守卫：纯函数语义 + 源码静态扫描（进程集合只允许在 Process 分支重建一次）。
/// </summary>
public class TabRebuildPolicyTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string VmSource()
    {
        var dir = Path.Combine(RepoRoot(), "src", "MemPilot.App", "ViewModels");
        var files = Directory.GetFiles(dir, "MainViewModel*.cs");
        Assert.NotEmpty(files);
        return string.Join("\n", files.OrderBy(f => f).Select(File.ReadAllText));
    }

    [Theory]
    [InlineData("📋", TabPage.Process)]
    [InlineData("🧹", TabPage.Cleanup)]
    [InlineData("⏸", TabPage.Suspended)]
    [InlineData("🧠", TabPage.Decisions)]
    [InlineData("🏠", TabPage.Home)]
    [InlineData("🎚", TabPage.Other)]
    [InlineData("", TabPage.Other)]
    [InlineData(null, TabPage.Other)]
    public void Parse_MapsEveryTabKey(string? key, TabPage expect)
        => Assert.Equal(expect, TabRebuildPolicy.Parse(key));

    [Fact]
    public void OnlyProcessPage_RebuildsProcessCollection()
    {
        foreach (var page in Enum.GetValues<TabPage>())
            Assert.Equal(page == TabPage.Process, TabRebuildPolicy.RebuildsProcessCollection(page));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3.9, false)]
    [InlineData(4.0, true)]
    [InlineData(60, true)]
    public void ShouldRebuild_ThrottlesUnderFourSeconds(double elapsedSeconds, bool expect)
    {
        var last = new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expect, TabRebuildPolicy.ShouldRebuild(last, last.AddSeconds(elapsedSeconds)));
    }

    [Fact]
    public void FirstRefresh_EverRebuilds()
        => Assert.True(TabRebuildPolicy.ShouldRebuild(DateTime.MinValue, DateTime.UtcNow));

    [Fact]
    public void ViewModel_UsesPolicy_NotDuplicatedLogic()
    {
        var src = VmSource();
        Assert.Contains("TabRebuildPolicy.ShouldRebuild", src);
        Assert.Contains("TabRebuildPolicy.Parse(ActiveTabKey)", src);
    }

    [Fact]
    public void ProcessCollection_IsRebuiltExactlyOnce_InsideProcessBranch()
    {
        var src = VmSource();
        // 注意：FilteredProcesses 的赋值也包含 "Processes = new ObservableCollection<ProcessInfo>"，
        // 所以必须用「前面不是字母」的边界，否则守卫会把自己人当回归。
        var matches = System.Text.RegularExpressions.Regex.Matches(
            src, @"(?<![A-Za-z])Processes = new ObservableCollection<ProcessInfo>");
        Assert.True(matches.Count == 1, $"进程集合应只在进程分支重建一次，实际 {matches.Count} 处");

        var at = matches[0].Index;
        var branch = src.IndexOf("case TabPage.Process:", StringComparison.Ordinal);
        var nextBranch = src.IndexOf("case TabPage.Cleanup:", StringComparison.Ordinal);
        Assert.True(branch > 0 && nextBranch > branch);
        Assert.InRange(at, branch, nextBranch - 1);
    }

    [Fact]
    public void UpdateCollections_DoesNotTouchProcessCollection()
    {
        var src = VmSource();
        var start = src.IndexOf("private void UpdateCollections", StringComparison.Ordinal);
        var end = src.IndexOf("private void RebuildActiveTab", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var body = src[start..end];
        Assert.DoesNotContain("Processes = ", body);   // 刷新管道只更新 _lastProcs，重建必须走策略
        Assert.Contains("TabRebuildPolicy.ShouldRebuild", body);
    }
}
