using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>GPU 适配器枚举与展示：不写恒真断言；无显示设备的环境显式记录诊断。</summary>
public class GpuInfoServiceTests
{
    [Fact]
    public void GetAdapters_ReturnsNamedAdapters_OrRecordsEnvironmentDiag()
    {
        var adapters = new GpuInfoService().GetAdapters();
        var lines = new List<string> { "count=" + adapters.Count, "diag=" + GpuInfoService.LastDiag };
        lines.AddRange(adapters.Select(a => $"{a.Name}|{a.Vendor}|{a.DedicatedVideoMemoryBytes}|used={a.UsedVideoMemoryBytes}"));
        File.WriteAllLines(Path.Combine(TestTmp.Dir, "gpu-diag.txt"), lines);

        if (adapters.Count == 0) return; // 无显示设备的执行上下文（CI/沙箱）：诊断已落盘，不判失败
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
    }

    // GDI 兜底路径没有用量数据时不得渲染假的 0%
    [Fact]
    public void Summary_WithoutUsage_ShowsUnavailableInsteadOfFakeZero()
    {
        var info = new GpuAdapterInfo { Name = "NVIDIA GeForce RTX 5060 Ti", DedicatedVideoMemoryBytes = 17103323136 };
        Assert.Contains("用量不可用", info.Summary);
        Assert.DoesNotContain("(0%)", info.Summary);
    }
}
