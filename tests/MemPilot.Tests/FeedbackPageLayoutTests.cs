using System.Text.RegularExpressions;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 反馈页布局守卫（v0.3.7）：说明文字与输入框都默认落在 Grid 第 0 行，实测叠在一起看不清。
/// 这里锁住四个区块各占一行：说明(0) / 表单(1) / 列表(2) / 操作(3)，且输入框带可见标签。
/// </summary>
public class FeedbackPageLayoutTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string FeedbackBlock()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MemPilot.App", "MainWindow.xaml"));
        var start = xaml.IndexOf("<TabItem Header=\"💬", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到反馈页签");
        var end = xaml.IndexOf("系统内存清理", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return xaml.Substring(start, end - start);
    }

    [Fact]
    public void Blocks_OccupyDistinctGridRows()
    {
        var block = FeedbackBlock();
        Assert.Contains("<Border Grid.Row=\"0\"", block);   // 说明卡
        Assert.Contains("<Grid Grid.Row=\"1\"", block);     // 表单
        Assert.Contains("Grid.Row=\"2\" x:Name=\"FeedbackGrid\"", block);
        Assert.Contains("<StackPanel Grid.Row=\"3\"", block);
        Assert.Equal(4, Regex.Matches(block, "<RowDefinition Height=").Count);
    }

    [Fact]
    public void Inputs_HaveVisibleLabels()
    {
        var block = FeedbackBlock();
        Assert.Contains("进程名（必填", block);
        Assert.Contains("可执行文件路径", block);
        Assert.Contains("把它归类为", block);
        Assert.Contains("备注（可选", block);
    }

    [Fact]
    public void Page_ExplainsEffect_AndUndo()
    {
        var block = FeedbackBlock();
        Assert.Contains("这些反馈做什么", block);
        Assert.Contains("不上传", block);
        Assert.Contains("撤销这条反馈", block);
    }
}
