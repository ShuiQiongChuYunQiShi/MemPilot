using System.IO;
using System.Text.RegularExpressions;
using MemPilot.Localization;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.19.0 i18n 守卫：
/// ① 中英字典的 key 集合必须完全一致（漏翻一条 = 英文界面出现 key）；
/// ② 字典值不能为空；
/// ③ UiTerms 的所有枚举映射都能从字典解析，且切语言后输出随语言变化；
/// ④ 未知枚举/未知分类依据必须原样返回（不显示 key）。
/// </summary>
[Collection("Localization")]
public class LocalizationGuardTests
{
    private static string Root => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static Dictionary<string, string> LoadDict(string culture)
    {
        var path = Path.Combine(Root, "src", "MemPilot.App", "Resources", $"Strings.{culture}.xaml");
        var text = File.ReadAllText(path);
        return Regex.Matches(text, "<sys:String\\s+x:Key=\"([^\"]+)\">(.*?)</sys:String>",
                RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static IDisposable UseDict(string culture)
    {
        var dict = LoadDict(culture);
        UiStrings.Resolver = k => dict.TryGetValue(k, out var v) ? v : null;
        return new Reset();
    }

    private sealed class Reset : IDisposable
    {
        public void Dispose() => UiStrings.ResetForTests();
    }

    [Fact]
    public void BothDictionaries_HaveIdenticalKeys()
    {
        var zh = LoadDict("zh-CN").Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var en = LoadDict("en").Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(zh, en);
    }

    [Fact]
    public void BothDictionaries_HaveNoEmptyValues()
    {
        foreach (var culture in new[] { "zh-CN", "en" })
        {
            var dict = LoadDict(culture);
            Assert.True(dict.Count >= 60, $"{culture} 只有 {dict.Count} 个 key");
            Assert.All(dict, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{culture}:{kv.Key} 为空"));
        }
    }

    [Fact]
    public void UiTerms_ResolveFromChineseDictionary()
    {
        using var _ = UseDict("zh-CN");
        Assert.Equal("回收工作集 (L1)", UiTerms.OfLevel("L1Trim"));
        Assert.Equal("挂起 (L2)", UiTerms.OfLevel("L2Suspend"));
        Assert.Equal("优雅关闭 (L3)", UiTerms.OfLevel("L3GracefulClose"));
        Assert.Equal("强制结束 (L4)", UiTerms.OfLevel("L4ForceKill"));
        Assert.Equal("MysteryLevel", UiTerms.OfLevel("MysteryLevel"));   // 未知原样返回
        Assert.Equal("租约", UiTerms.OfKind("Lease"));
        Assert.Equal("RecurringUnknown", UiTerms.OfKind("RecurringUnknown"));
        Assert.Equal("恢复挂起", UiTerms.OfAction("Resume"));
        Assert.Equal("程序清单：ComfyUI", UiTerms.OfCategoryReason("program_catalog:ComfyUI"));
        Assert.Equal("安全软件", UiTerms.OfCategoryReason("security_software"));
    }

    [Fact]
    public void UiTerms_SwitchToEnglish_ChangesOutput()
    {
        using var _ = UseDict("en");
        Assert.Equal("Suspend (L2)", UiTerms.OfLevel("L2Suspend"));
        Assert.Equal("Lease", UiTerms.OfKind("Lease"));
        Assert.Equal("Program catalog: ComfyUI", UiTerms.OfCategoryReason("program_catalog:ComfyUI"));
    }

    [Fact]
    public void UiStrings_WithoutResolver_FallsBackToKey()
    {
        UiStrings.ResetForTests();
        Assert.Equal("Nav_Home", UiStrings.Get("Nav_Home"));
        Assert.Equal("Level_L2Suspend", UiTerms.OfLevel("L2Suspend"));
        Assert.Equal("NoSuchKind", UiTerms.OfKind("NoSuchKind"));   // TryGet 未命中回原值
    }
}
