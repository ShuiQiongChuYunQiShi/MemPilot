using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 术语统一守卫（v0.11.0 P1-1，UX 摸排）。
///
/// 复盘发现同一动作有 6 个名字、同一概念 3 个名字（详见 UiTerms.cs 头注释）。
/// 用户确认的决策：页面概念统一「腾内存」、动作「回收工作集/挂起/优雅关闭/强制结束」、
/// 指标「提交余量」、用户标记「保留」。本测试扫描 UI 层源码，把旧词挡回去。
/// </summary>
public class UiTermsTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;
    private static string AppDir() => Path.Combine(RepoRoot(), "src", "MemPilot.App");

    private static IEnumerable<string> UiFiles()
        => Directory.GetFiles(AppDir(), "*.xaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(AppDir(), "*.cs", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(Path.Combine(AppDir(), "ViewModels"), "*.cs"));

    /// <summary>用户可见文案里不允许再出现的旧词（注释里的技术讨论不算用户可见）。</summary>
    private static readonly (string term, string why)[] BannedInUiText =
    {
        ("一键优化", "已统一为「回收闲置工作集」"),
        ("压缩工作集", "已统一为「回收（闲置）工作集」"),
        ("提交空闲", "已统一为「提交余量」"),
        ("需保留", "已统一为「保留」"),
    };

    /// <summary>「腾挪」只在代码注释/历史文档允许；XAML 属性与字符串插值文案不允许。</summary>
    [Fact]
    public void Xaml_DoesNotUseRetiredTerms()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDir(), "MainWindow.xaml"));
        foreach (var (term, why) in BannedInUiText)
            Assert.DoesNotContain(term, xaml);   // XAML 是纯 UI 文案，一律禁止
        Assert.DoesNotContain("腾挪", xaml.Replace("腾内存", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBehind_ActionFeedback_DoesNotUseRetiredTerms()
    {
        foreach (var f in UiFiles())
        {
            var src = File.ReadAllText(f);
            foreach (var line in src.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*")) continue; // 注释放过
                foreach (var (term, why) in BannedInUiText)
                    Assert.True(!line.Contains(term),
                        $"{Path.GetFileName(f)} 用户可见文案使用了旧词「{term}」——{why}");
            }
        }
    }

    [Fact]
    public void UiTerms_LevelNames_CoverTheActionLadder()
    {
        Assert.Equal("回收工作集 (L1)", MemPilot.Services.UiTerms.OfLevel("L1Trim"));
        Assert.Equal("挂起 (L2)", MemPilot.Services.UiTerms.OfLevel("L2Suspend"));
        Assert.Equal("优雅关闭 (L3)", MemPilot.Services.UiTerms.OfLevel("L3GracefulClose"));
        Assert.Equal("强制结束 (L4)", MemPilot.Services.UiTerms.OfLevel("L4ForceKill"));
        Assert.Equal("MysteryLevel", MemPilot.Services.UiTerms.OfLevel("MysteryLevel")); // 未知原样返回
    }

    [Fact]
    public void MakeRoom_IsTheCanonicalPageName()
    {
        // 导航、页面标题、热键路径必须是同一个词
        var xaml = File.ReadAllText(Path.Combine(AppDir(), "MainWindow.xaml"));
        Assert.Contains("腾内存", xaml);
        var page = File.ReadAllText(Path.Combine(AppDir(), "MainWindow.MakeRoomPage.cs"));
        Assert.Contains("腾内存计划", page);
        Assert.DoesNotContain("腾挪", page.Replace("腾内存", ""), StringComparison.Ordinal);
    }
}
