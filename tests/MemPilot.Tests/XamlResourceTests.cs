using System.Text.RegularExpressions;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// XAML 静态守卫（2026-10-31 真机事故教训）：资源键存在性 + 资源类型匹配，在编译期无法发现，
/// 只能在启动时 XamlParseException 崩溃（当时 DropShadowEffect.Color 被赋了 SolidColorBrush 资源）。
/// 纯文本扫描两个 XAML 文件，任何键缺失或类型错配即红，不需要启动 WPF。
/// </summary>
public class XamlResourceTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static (string app, string win) Load()
    {
        var root = RepoRoot();
        return (File.ReadAllText(Path.Combine(root, "src", "MemPilot.App", "App.xaml")),
                File.ReadAllText(Path.Combine(root, "src", "MemPilot.App", "MainWindow.xaml")));
    }

    [Fact]
    public void EveryReferencedResourceKey_IsDefined()
    {
        var (app, win) = Load();
        var defined = Regex.Matches(app + win, "x:Key=\"([^\"]+)\"")
                           .Select(m => m.Groups[1].Value).ToHashSet();
        var used = Regex.Matches(win + app, "\\{(?:Static|Dynamic)Resource\\s+([^}]+)\\}")
                        .Select(m => m.Groups[1].Value.Trim())
                        .Where(k => !k.StartsWith("{")) // 跳过 {x:Type ...} 这类标记扩展键
                        .ToHashSet();

        var missing = used.Where(k => !defined.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "未定义的资源键：" + string.Join(", ", missing));
    }

    [Fact]
    public void ColorTypedProperties_NeverReferenceBrushResources()
    {
        var (app, win) = Load();
        // Color="{DynamicResource K}" 只能指向 <Color x:Key="K">，不能是 SolidColorBrush（真机 bug）
        var colorRefs = Regex.Matches(win, "Color=\"\\{(?:Static|Dynamic)Resource\\s+([^}]+)\\}\"")
                            .Select(m => m.Groups[1].Value.Trim()).ToList();
        foreach (var key in colorRefs)
        {
            var colorDefined = Regex.IsMatch(app + win, "<Color x:Key=\"" + Regex.Escape(key) + "\"");
            Assert.True(colorDefined,
                $"资源 {key} 被赋给 Color 属性，但不是 <Color> 类型（SolidColorBrush 会让启动崩溃）");
        }
    }
}
