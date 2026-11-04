using System.Text.Json;
using System.Text.RegularExpressions;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 进程页列设置 / 用途筛选的静态守卫（棱镜 ui/table-density + product/classification-quality）：
/// ① 默认隐藏列必须真实存在（列改名后配置静默失效是最典型的"看起来好的坏配置"）；
/// ② 列设置菜单项的 Tag 必须是**已登记的稳定 id**（v0.10.0 起 Tag≠Header）；
/// ③ PID/名称 固定可见，不允许被配置隐藏；
/// ④ 用途筛选项的 Tag 必须是稳定 id，且能映射回枚举。
///
/// v0.10.0 为什么改：旧约定是「Tag 必须与表头逐字一致」，也就是把**中文显示文本**当持久化 key。
/// 一旦界面语言变化，用户调好的列设置和筛选会被静默重置——这条旧断言本身就是在固化那个缺陷。
/// </summary>
public class ProcessPageSettingTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string Xaml() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.xaml"));

    private static string ProcessPageCs() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.ProcessPage.cs"));

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
    public void DefaultHiddenColumns_AreRegisteredStableIds()
    {
        var cfg = new AppConfig();
        Assert.NotEmpty(cfg.ProcessHiddenColumns);
        foreach (var id in cfg.ProcessHiddenColumns)
            Assert.True(PersistedIds.Column.All.Contains(id),
                $"默认隐藏列不是已登记的稳定 id：{id}（v0.10.0 起 config 不允许存中文表头）");
    }

    [Fact]
    public void DefaultHiddenColumns_MapToRealGridColumns()
    {
        // 稳定 id 必须能通过 HeaderToId 反查到真实存在的列，否则"默认收起"是空操作。
        var headers = GridHeaders();
        var map = HeaderToIdFromSource();
        var mappedIds = headers
            .Where(map.ContainsKey)
            .Select(h => map[h])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in new AppConfig().ProcessHiddenColumns)
            Assert.True(mappedIds.Contains(id), $"默认隐藏列 {id} 反查不到任何 DataGrid 列");
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
    public void ColumnMenuTags_AreRegisteredIds_NotDisplayText()
    {
        var xaml = Xaml();
        var start = xaml.IndexOf("ColumnMenuButton", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到列设置按钮");
        var end = xaml.IndexOf("</ContextMenu>", start, StringComparison.Ordinal);
        var block = xaml.Substring(start, end - start);
        var items = Regex.Matches(block, "Header=\"([^\"]+)\" Tag=\"([^\"]+)\"");
        Assert.NotEmpty(items);

        foreach (Match m in items)
        {
            var header = m.Groups[1].Value;
            var tag = m.Groups[2].Value;

            Assert.NotEqual(header, tag);          // v0.10.0：绝不能再拿显示文本当 key
            Assert.False(PersistedIds.Column.LegacyMap.ContainsKey(tag),
                $"Tag 是中文表头（{tag}），不是稳定 id");
            Assert.Contains(tag, PersistedIds.Column.All);  // 必须是登记过的稳定 id
        }
    }

    [Fact]
    public void ColumnMenuTags_ResolveToExistingGridColumn()
    {
        var headers = GridHeaders();
        var map = HeaderToIdFromSource();
        var xaml = Xaml();
        var start = xaml.IndexOf("ColumnMenuButton", StringComparison.Ordinal);
        var end = xaml.IndexOf("</ContextMenu>", start, StringComparison.Ordinal);
        var block = xaml.Substring(start, end - start);

        foreach (Match m in Regex.Matches(block, "Header=\"([^\"]+)\" Tag=\"([^\"]+)\""))
        {
            var header = m.Groups[1].Value;
            var tag = m.Groups[2].Value;
            Assert.True(headers.Contains(header), $"列设置菜单项 {header} 在进程表里不存在");
            Assert.True(map.TryGetValue(header, out var mapped) && mapped == tag,
                $"{header} 的显示文本→id 映射与菜单 Tag 不一致（代码里登记的是另一套）");
        }
    }

    [Fact]
    public void WorkloadFilter_DefaultsToAll_AndRoundTripsStableId()
    {
        var cfg = new AppConfig();
        Assert.Equal("", cfg.WorkloadFilter);

        cfg.WorkloadFilter = PersistedIds.Workload.Entertainment;
        cfg.ProcessHiddenColumns = new List<string> { PersistedIds.Column.IsProtected };
        var json = JsonSerializer.Serialize(cfg);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;

        Assert.Equal(PersistedIds.Workload.Entertainment, back.WorkloadFilter);
        Assert.Contains(PersistedIds.Column.IsProtected, back.ProcessHiddenColumns);
        // 落盘里不允许出现任何中文 key
        Assert.DoesNotContain("娱乐", json);
        Assert.DoesNotContain("保护\"", json);
    }

    [Fact]
    public void WorkloadFilterOptions_UseStableIdsThatMapBackToEnum()
    {
        var xaml = Xaml();
        var start = xaml.IndexOf("WorkloadFilterBox", StringComparison.Ordinal);
        var end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        var block = xaml.Substring(start, end - start);
        var tags = Regex.Matches(block, "ComboBoxItem Content=\"([^\"]+)\" Tag=\"([^\"]+)\"")
                         .Select(m => m.Groups[2].Value)
                         .Where(t => t != "")
                         .ToHashSet(StringComparer.Ordinal);

        // 每个 Tag 都必须能映射回一个真实枚举值 —— 否则筛选永远筛出空
        Assert.NotEmpty(tags);
        foreach (var tag in tags)
            Assert.NotNull(WorkloadText.FromId(tag));

        // 且必须覆盖全部枚举值
        var allIds = Enum.GetValues<WorkloadKind>().Select(WorkloadText.IdOf)
                         .ToHashSet(StringComparer.Ordinal);
        Assert.True(tags.SetEquals(allIds),
            "用途筛选 id 与枚举不一致：" +
            string.Join(", ", tags.Except(allIds)) + " / " + string.Join(", ", allIds.Except(tags)));
    }

    /// <summary>从 MainWindow.ProcessPage.cs 里解析 HeaderToId 映射（正则即可，源文件格式稳定）。</summary>
    private static Dictionary<string, string> HeaderToIdFromSource()
    {
        var src = ProcessPageCs();
        var start = src.IndexOf("HeaderToId = new", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到 HeaderToId 映射表");
        var end = src.IndexOf("};", start, StringComparison.Ordinal);
        var block = src.Substring(start, end - start);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(block, "\\[\"([^\"]+)\"\\]\\s*=\\s*PersistedIds\\.Column\\.(\\w+)"))
        {
            var header = m.Groups[1].Value;
            var constName = m.Groups[2].Value;
            var value = typeof(PersistedIds.Column)
                .GetField(constName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) as string;
            if (value != null) map[header] = value;
        }
        return map;
    }
}
