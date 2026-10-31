using System.Text.Json;
using System.Text.RegularExpressions;
using MemPilot.Models;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 进程页列设置 / 用途筛选的静态守卫（棱镜 ui/table-density + product/classification-quality）：
/// ① 默认隐藏列必须真实存在（列改名后配置静默失效是最典型的"看起来好的坏配置"）；
/// ② 列设置菜单项的 Tag 必须与表头逐字一致（否则点击无效果）；
/// ③ PID/名称 固定可见，不允许被配置隐藏。
/// </summary>
public class ProcessPageSettingTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string Xaml() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.xaml"));

    private static HashSet<string> GridHeaders()
    {
        var xaml = Xaml();
        var start = xaml.IndexOf("<DataGrid x:Name=\"ProcessGrid\"", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到 ProcessGrid");
        var end = xaml.IndexOf("</DataGrid>", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到 ProcessGrid 结束标签");
        var block = xaml.Substring(start, end - start);
        return Regex.Matches(block, "Header=\"([^\"]+)\"")
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void DefaultHiddenColumns_AllExistInGrid()
    {
        var headers = GridHeaders();
        var cfg = new AppConfig();
        Assert.NotEmpty(cfg.ProcessHiddenColumns);
        foreach (var h in cfg.ProcessHiddenColumns)
            Assert.True(headers.Contains(h), $"默认隐藏列不存在于进程表：{h}");
    }

    [Fact]
    public void FrozenColumns_AreNeverConfigurable()
    {
        var cfg = new AppConfig();
        Assert.DoesNotContain("PID", cfg.ProcessHiddenColumns);
        Assert.DoesNotContain("名称", cfg.ProcessHiddenColumns);
        Assert.Contains("FrozenColumnCount=\"2\"", Xaml());
    }

    [Fact]
    public void ColumnMenuTags_MatchGridHeaders()
    {
        var headers = GridHeaders();
        var xaml = Xaml();
        var start = xaml.IndexOf("ColumnMenuButton", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到列设置按钮");
        var end = xaml.IndexOf("</Button>", start, StringComparison.Ordinal);
        var block = xaml.Substring(start, end - start);
        var items = Regex.Matches(block, "Header=\"([^\"]+)\" Tag=\"([^\"]+)\"");
        Assert.NotEmpty(items);
        foreach (Match m in items)
        {
            var header = m.Groups[1].Value;
            var tag = m.Groups[2].Value;
            Assert.Equal(header, tag);
            Assert.True(headers.Contains(tag), $"列设置菜单项在进程表里不存在：{tag}");
        }
    }

    [Fact]
    public void WorkloadFilter_DefaultsToAll_AndRoundTrips()
    {
        var cfg = new AppConfig();
        Assert.Equal("", cfg.WorkloadFilter);

        cfg.WorkloadFilter = "娱乐";
        cfg.ProcessHiddenColumns.Add("保护");
        var json = JsonSerializer.Serialize(cfg);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal("娱乐", back.WorkloadFilter);
        Assert.Contains("保护", back.ProcessHiddenColumns);
    }

    [Fact]
    public void WorkloadFilterOptions_AreExactlyClassifierDisplayNames()
    {
        // 下拉框的选项必须与 WorkloadClassifier.DisplayName 的取值一致，否则筛出来永远是空
        var xaml = Xaml();
        var start = xaml.IndexOf("WorkloadFilterBox", StringComparison.Ordinal);
        var end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        var block = xaml.Substring(start, end - start);
        var tags = Regex.Matches(block, "ComboBoxItem Content=\"([^\"]+)\" Tag=\"([^\"]+)\"")
                         .Select(m => m.Groups[2].Value)
                         .Where(t => t != "")
                         .ToHashSet(StringComparer.Ordinal);

        var names = Enum.GetValues<WorkloadKind>()
                        .Select(MemPilot.Services.WorkloadClassifier.DisplayName)
                        .ToHashSet(StringComparer.Ordinal);
        Assert.True(tags.SetEquals(names), "用途筛选项与分类器显示名不一致：" +
            string.Join(", ", tags.Except(names)) + " / " + string.Join(", ", names.Except(tags)));
    }
}
