using System.IO;
using System.Text.RegularExpressions;
using MemPilot.Localization;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 术语统一守卫（v0.11.0 P1-1，UX 摸排；v0.19.0 改为字典驱动）。
///
/// 复盘发现同一动作有 6 个名字、同一概念 3 个名字（详见 UiTerms.cs 头注释）。
/// 用户确认的决策：页面概念统一「腾内存」、动作「回收工作集/挂起/优雅关闭/强制结束」、
/// 指标「提交余量」、用户标记「保留」。本测试扫描 UI 层源码与字典，把旧词挡回去。
/// </summary>
[Collection("Localization")]
public class UiTermsTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;
    private static string AppDir() => Path.Combine(RepoRoot(), "src", "MemPilot.App");

    private static IEnumerable<string> UiFiles()
        => Directory.GetFiles(AppDir(), "*.xaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(AppDir(), "*.cs", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(Path.Combine(AppDir(), "ViewModels"), "*.cs"));

    private static Dictionary<string, string> LoadDict(string culture = "zh-CN")
    {
        var path = Path.Combine(AppDir(), "Resources", $"Strings.{culture}.xaml");
        var text = File.ReadAllText(path);
        return Regex.Matches(text, "<sys:String\\s+x:Key=\"([^\"]+)\">(.*?)</sys:String>",
                RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private sealed class DictScope : IDisposable
    {
        public DictScope(string culture)
        {
            var dict = LoadDict(culture);
            UiStrings.Resolver = k => dict.TryGetValue(k, out var v) ? v : null;
        }
        public void Dispose() => UiStrings.ResetForTests();
    }

    /// <summary>用户可见文案里不允许再出现的旧词（注释里的技术讨论不算用户可见）。</summary>
    private static readonly (string term, string why)[] BannedInUiText =
    {
        ("一键优化", "已统一为「回收闲置工作集」"),
        ("压缩工作集", "已统一为「回收（闲置）工作集」"),
        ("提交空闲", "已统一为「提交余量」"),
        ("需保留", "已统一为「保留」"),
    };

    /// <summary>XAML 已无裸中文（全部走资源键），旧词检查改为覆盖两个字典。</summary>
    [Fact]
    public void Dictionaries_DoNotUseRetiredTerms()
    {
        foreach (var culture in new[] { "zh-CN", "en" })
        {
            var dict = LoadDict(culture);
            var all = string.Join("\n", dict.Values);
            foreach (var (term, why) in BannedInUiText)
                Assert.True(!all.Contains(term), $"{culture} 字典使用了旧词「{term}」——{why}");
        }
    }

    [Fact]
    public void Xaml_HasNoBareChineseAttributes()
    {
        // 抽取完成后的守卫：XAML 属性值里不能再出现中文（注释与资源字典除外）
        foreach (var file in Directory.GetFiles(AppDir(), "*.xaml", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("Strings.")) continue;   // 字典本身
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\b\w+=""([^""{}]*[\u4e00-\u9fa5][^""{}]*)"""))
                Assert.Fail($"{name}: 属性值仍有裸中文「{m.Groups[1].Value}」——应改为 {{DynamicResource Key}}");
        }
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
        using var _ = new DictScope("zh-CN");
        Assert.Equal("回收工作集 (L1)", UiTerms.OfLevel("L1Trim"));
        Assert.Equal("挂起 (L2)", UiTerms.OfLevel("L2Suspend"));
        Assert.Equal("优雅关闭 (L3)", UiTerms.OfLevel("L3GracefulClose"));
        Assert.Equal("强制结束 (L4)", UiTerms.OfLevel("L4ForceKill"));
        Assert.Equal("MysteryLevel", UiTerms.OfLevel("MysteryLevel")); // 未知原样返回
    }

    [Fact]
    public void MakeRoom_IsTheCanonicalPageName()
    {
        // 导航资源值、页面标题、热键路径必须是同一个词
        var zh = LoadDict("zh-CN");
        Assert.Equal("🚀  腾内存", zh["Nav_MakeRoom"]);
        Assert.Contains("腾内存", zh["MakeRoom_Title"]);
        var page = File.ReadAllText(Path.Combine(AppDir(), "MainWindow.MakeRoomPage.cs"));
        // v0.19.0：页面文案已资源化，断言稳定 key 而不是中文
        Assert.Contains("MakeRoom_PlanReadyOtherPage", page);
        Assert.Contains("MakeRoom_Progress", page);
        Assert.DoesNotContain("腾挪", page.Replace("腾内存", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void CategoryNames_AreConvergedInUiTerms()
    {
        using var _ = new DictScope("zh-CN");
        // 旧实现有两份重复映射（CategoryTextConverter 短名 + MainViewModel 全名），已收敛
        Assert.Equal("系统关键", UiTerms.OfCategory(Models.ProcessCategory.SystemCritical));
        Assert.Equal("系统关键", UiTerms.OfCategory(Models.ProcessCategory.SystemCritical, shortForm: true));
        Assert.Equal("游戏", UiTerms.OfCategory(Models.ProcessCategory.Game));
        Assert.Equal("游戏", UiTerms.OfCategory(Models.ProcessCategory.Game, shortForm: true));
    }
}
