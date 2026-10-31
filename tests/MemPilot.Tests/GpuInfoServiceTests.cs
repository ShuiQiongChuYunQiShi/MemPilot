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
        lines.AddRange(adapters.Select(a => $"{a.Name}|{a.Vendor}|{a.DedicatedVideoMemoryBytes}|used={a.UsedVideoMemoryBytes}|src={a.UsageSource}"));
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

    [Fact]
    public void Summary_PdhFallback_IsLabelled()
    {
        var info = new GpuAdapterInfo
        {
            Name = "RTX",
            DedicatedVideoMemoryBytes = 16L << 30,
            UsedVideoMemoryBytes = 8L << 30,
            UsageSource = "pdh"
        };
        Assert.Contains("50%", info.Summary);
        Assert.Contains("PDH 汇总", info.Summary);
    }

    [Fact]
    public void Diag_RecordsHresult_WhenFactoryFails()
    {
        // 与本机实测一致：DXGI 工厂失败时必须留下 HRESULT，否则只能看到"失败了"却不知道为什么
        _ = new GpuInfoService().GetAdapters();
        if (GpuInfoService.LastDiag.StartsWith("dxgi: factory creation failed", StringComparison.Ordinal))
            Assert.Contains("hr=0x", GpuInfoService.LastDiag);
    }

    // ---- PDH 兜底（纯函数）----

    [Fact]
    public void Fallback_SingleAdapter_UsesTotal()
    {
        var list = new List<GpuAdapterInfo> { new() { Name = "A", DedicatedVideoMemoryBytes = 16L << 30 } };
        var filled = GpuInfoService.ApplyPdhFallback(list, null, 8L << 30);
        Assert.Equal(1, filled);
        Assert.Equal(8L << 30, list[0].UsedVideoMemoryBytes);
        Assert.Equal("pdh", list[0].UsageSource);
    }

    [Fact]
    public void Fallback_MultiAdapter_MatchesByLuid()
    {
        var list = new List<GpuAdapterInfo>
        {
            new() { Name = "Intel", DedicatedVideoMemoryBytes = 2L << 30, LuidHighPart = 0, LuidLowPart = 0x1111 },
            new() { Name = "NVIDIA", DedicatedVideoMemoryBytes = 16L << 30, LuidHighPart = 0, LuidLowPart = 0x2222 },
        };
        var byLuid = new Dictionary<string, long>
        {
            ["luid_0x00000000_0x00001111_phys_0"] = 1L << 30,
            ["luid_0x00000000_0x00002222_phys_0"] = 9L << 30,
        };
        var filled = GpuInfoService.ApplyPdhFallback(list, byLuid, 10L << 30);
        Assert.Equal(2, filled);
        Assert.Equal(1L << 30, list[0].UsedVideoMemoryBytes);
        Assert.Equal(9L << 30, list[1].UsedVideoMemoryBytes);
    }

    [Fact]
    public void Fallback_MultiAdapter_WithoutLuid_DoesNotGuess()
    {
        var list = new List<GpuAdapterInfo>
        {
            new() { Name = "A", DedicatedVideoMemoryBytes = 8L << 30 },
            new() { Name = "B", DedicatedVideoMemoryBytes = 8L << 30 },
        };
        Assert.Equal(0, GpuInfoService.ApplyPdhFallback(list, null, 12L << 30));
        Assert.All(list, a => Assert.Equal(0, a.UsedVideoMemoryBytes));
    }

    [Fact]
    public void Fallback_NeverFabricates_FromCapacity()
    {
        var list = new List<GpuAdapterInfo> { new() { Name = "A", DedicatedVideoMemoryBytes = 8L << 30 } };
        Assert.Equal(0, GpuInfoService.ApplyPdhFallback(list, null, 0));
        Assert.Equal(0, list[0].UsedVideoMemoryBytes);
        Assert.Contains("用量不可用", list[0].Summary);
    }

    [Fact]
    public void Fallback_KeepsExistingDxgiUsage()
    {
        var list = new List<GpuAdapterInfo>
        { new() { Name = "A", DedicatedVideoMemoryBytes = 8L << 30, UsedVideoMemoryBytes = 3L << 30, UsageSource = "dxgi" } };
        Assert.Equal(0, GpuInfoService.ApplyPdhFallback(list, null, 5L << 30));
        Assert.Equal(3L << 30, list[0].UsedVideoMemoryBytes);
        Assert.Equal("dxgi", list[0].UsageSource);
    }
}
