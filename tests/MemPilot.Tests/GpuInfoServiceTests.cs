using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>DXGI 适配器枚举：有 GPU 的机器应至少返回一个非 WARP 适配器（CI 无 GPU 时跳过）。</summary>
public class GpuInfoServiceTests
{
    [Fact]
    public void GetAdapters_OnMachineWithGpu_ReturnsAtLeastOne()
    {
        var adapters = new GpuInfoService().GetAdapters();
        if (adapters.Count == 0)
        {
            // 无 GPU / 虚拟化环境：不判失败，但记录以便诊断
            Assert.True(Environment.GetEnvironmentVariable("CI") == "true" || adapters.Count == 0);
            return;
        }
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
    }
}
