using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 图表点集守卫（v0.3.6 真机事故）：WPF 的 Polyline/Polygon 绑定 PointCollection 时，
/// 用 Clear()+Add() 变异**不会**让 Shape 重建几何——实测视觉树上 Points.Count=231，
/// 但 RenderedGeometry 为空、ActualWidth=0，曲线一片空白（历史页"点了没曲线"）。
/// 正确做法是整体替换（new PointCollection + PropertyChanged），与项目既有集合规则同源。
/// 这里静态扫描 VM，禁止对图表点集调用 Clear()。
/// </summary>
public class ChartCollectionGuardTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string VmSource()
    {
        var dir = Path.Combine(RepoRoot(), "src", "MemPilot.App", "ViewModels");
        return string.Join("\n", Directory.GetFiles(dir, "MainViewModel*.cs")
                                      .OrderBy(f => f).Select(File.ReadAllText));
    }

    [Theory]
    [InlineData("HistoryPoints")]
    [InlineData("HistoryAreaPoints")]
    [InlineData("GpuHistoryPoints")]
    [InlineData("GpuHistoryAreaPoints")]
    [InlineData("MemoryTrendPoints")]
    [InlineData("MemoryTrendAreaPoints")]
    public void ChartCollections_MustNotBeClearedInPlace(string name)
    {
        var src = VmSource();
        Assert.DoesNotContain($"{name}.Clear()", src);
    }

    [Theory]
    [InlineData("HistoryPoints")]
    [InlineData("HistoryAreaPoints")]
    [InlineData("GpuHistoryPoints")]
    [InlineData("GpuHistoryAreaPoints")]
    [InlineData("MemoryTrendPoints")]
    [InlineData("MemoryTrendAreaPoints")]
    public void ChartCollections_MustBeReplaceable(string name)
    {
        var src = VmSource();
        // 属性必须有 setter（整体替换），不能是只读 get-only 集合
        Assert.Matches($@"public System\.Windows\.Media\.PointCollection {name}\s*\{{[^}}]*private set", src);
    }
}
