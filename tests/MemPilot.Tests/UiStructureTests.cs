using System.Text.RegularExpressions;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.6.0 UI 结构守卫：导航分组 / 稳定 Tag 路由 / 反馈降级为记录表 / 线程页下线 / 筛选芯片。
/// 这些约定一旦破坏（改 Header 文字、加回表单、丢了分组标题），用户可读性就回退。
/// </summary>
public class UiStructureTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string Xaml() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.xaml"));

    private static string WindowCs() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.xaml.cs"));

    private static string TabsCs() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.Tabs.cs"));

    [Fact]
    public void Nav_HasThreeGroupHeaders()
    {
        var xaml = Xaml();
        // v0.19.0：分组标题也走资源键（文案在字典里，切换语言即时生效）
        foreach (var key in new[] { "Nav_GroupCore", "Nav_GroupObserve", "Nav_GroupAdvanced" })
            Assert.Contains($"Text=\"{{DynamicResource {key}}}\" Style=\"{{StaticResource NavGroup}}\"", xaml);
    }

    [Fact]
    public void Nav_TabsUseStableTags_RoutedByTagNotEmoji()
    {
        var xaml = Xaml();
        var expected = new[] { "home", "process", "cleanup", "makeroom", "suspend",
                               "decisions", "history", "audit", "feedback", "mode", "sysmem", "settings" };
        foreach (var tag in expected)
            Assert.True(Regex.IsMatch(xaml, $"<TabItem Header=\"[^\"]+\" Tag=\"{tag}\""),
                $"缺页签 Tag：{tag}");

        // 路由必须走 Tag，不允许再依赖 emoji 前缀
        Assert.DoesNotContain("StartsWith(\"🚀\")", TabsCs());
        Assert.DoesNotContain("StartsWith(\"📊\")", WindowCs());
        Assert.Contains("Equals(item.Tag, key)", TabsCs());
    }

    [Fact]
    public void ThreadPage_IsRemoved_ManagementMovedToProcessMenu()
    {
        var xaml = Xaml();
        Assert.DoesNotContain("🧵", xaml);                       // 独立线程页下线
        Assert.DoesNotContain("ThreadSearchBox", xaml);
        Assert.Contains("{DynamicResource Menu_Threads}", xaml);  // 降级为进程页右键入口
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "src", "MemPilot.App", "ThreadWindow.xaml")));
        // 线程数列改名后，列设置与表格必须一致（ProcessPageSettingTests 也会校验）
        Assert.Contains("Header=\"{DynamicResource Col_Threads}\"", xaml);
    }

    [Fact]
    public void FeedbackPage_IsRecordsTableOnly_NoManualForm()
    {
        var xaml = Xaml();
        Assert.Contains("Tag=\"feedback\"", xaml);
        Assert.Contains("FeedbackGrid", xaml);
        Assert.Contains("{DynamicResource Menu_UndoFeedback}", xaml);
        Assert.DoesNotContain("FbName", xaml);                   // 手工表单删除
        Assert.DoesNotContain("SubmitFeedback_Click", xaml);
        Assert.Contains("{DynamicResource Feedback_Hint}", xaml);  // 仍要指路
    }

    [Fact]
    public void ProcessToolbar_HasFilterChips_NotLegend()
    {
        var xaml = Xaml();
        foreach (var chip in new[] { "ChipAll", "ChipSystem", "ChipGame", "ChipAi", "ChipUnknown" })
            Assert.Contains($"x:Name=\"{chip}\"", xaml);
        Assert.DoesNotContain("系统/保护", xaml);                 // 旧图例文字（圆点）删除
        Assert.Contains("ToolBtn", xaml);                        // 工具栏统一样式
    }

    /// <summary>
    /// v0.13.1 守卫："打开界面空白"根因——分组头（裸 TextBlock）占据 TabControl items[0]，
    /// WPF Selector 默认选中第一个 item 即它本身，内容区渲染出"核心"二字。
    /// 防线：① home TabItem 必须 IsSelected="True"；② SelectionChanged 必须有空 tag 兜底回主页。
    /// </summary>
    [Fact]
    public void Nav_HomeTabIsSelectedAtStartup_AndSelectionChangedHasFallback()
    {
        var xaml = Xaml();
        Assert.True(Regex.IsMatch(xaml, "<TabItem Header=\"[^\"]+\" Tag=\"home\"[^>]*IsSelected=\"True\""),
            "home TabItem 必须带 IsSelected=\"True\"（否则初始选中落在分组头 TextBlock 上，界面空白）");
        Assert.Contains("if (tag == \"\")", TabsCs());
        Assert.Contains("SwitchToTab(\"home\");", TabsCs());
    }
}
