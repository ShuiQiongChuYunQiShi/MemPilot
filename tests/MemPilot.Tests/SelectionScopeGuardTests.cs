using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.22.0 回归守卫：进程页与可清理页的选中项必须各管各的。
///
/// 旧实现 `GetSelectedUnused()` 先从 `UnusedGrid` 取，失败再回退到 `ProcessGrid`
/// （反向亦然），于是「在进程页右键挂起/强制结束」可能作用到可清理页残留的选择上。
/// 这两个页面各自维护选中语义，不允许再出现跨页回退。
/// </summary>
public class SelectionScopeGuardTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string CleanupCs() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.CleanupPage.cs"));

    private static string ProcessPageCs() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.ProcessPage.cs"));

    private static string Xaml() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.xaml"));

    [Fact]
    public void PageScopedSelectorsExist_AndLegacyCrossPageSelectorIsGone()
    {
        var src = CleanupCs();
        Assert.Contains("GetSelectedFromCleanupPage", src);
        Assert.Contains("GetSelectedFromProcessPage", src);
        Assert.DoesNotContain("GetSelectedUnused", src);
    }

    [Fact]
    public void CleanupPageSidebarButtons_UseCleanupScopedSelector()
    {
        var src = CleanupCs();
        var marker = "private async Task SelectedCleanup(";
        var idx = src.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, "找不到 SelectedCleanup");
        Assert.Contains("GetSelectedFromCleanupPage", src.Substring(idx, 400));
    }

    [Fact]
    public void ProcessContextMenuHandlers_UseProcessScopedSelector()
    {
        var src = CleanupCs();
        foreach (var name in new[] { "CtxL1_Click", "CtxL2_Click", "CtxL3_Click", "CtxL4_Click" })
        {
            var idx = src.IndexOf("private async void " + name, StringComparison.Ordinal);
            Assert.True(idx >= 0, "找不到处理器：" + name);
            Assert.Contains("GetSelectedFromProcessPage", src.Substring(idx, 260));
        }
    }

    [Fact]
    public void UnusedContextMenu_HasItsOwnHandlers()
    {
        var xaml = Xaml();
        foreach (var n in new[] { "UnusedL1_Click", "UnusedL2_Click", "UnusedL3_Click", "UnusedL4_Click" })
            Assert.Contains($"Click=\"{n}\"", xaml);
        // 进程页右键菜单仍然走 Ctx*（内部只看 ProcessGrid）
        Assert.Contains("Click=\"CtxL1_Click\"", xaml);
    }

    [Fact]
    public void ProcessGrid_SelectsTheRowUnderTheCursorOnRightClick()
    {
        Assert.Contains("PreviewMouseRightButtonDown=\"ProcessGrid_PreviewMouseRightButtonDown\"", Xaml());
        var src = ProcessPageCs();
        Assert.Contains("ProcessGrid_PreviewMouseRightButtonDown", src);
        Assert.Contains("DataGridRow", src);
    }
}
