using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>GPU 逐进程采集（PDH）：可用时值必须在 0-100；不可用环境（无 GPU/计数器禁用）跳过而非失败。</summary>
public class GpuUsageServiceTests
{
    [Fact]
    public void Sample_WhenAvailable_ReturnsBoundedValues()
    {
        using var svc = new GpuUsageService();
        svc.Sample(); // 基线
        Thread.Sleep(400);
        var map = svc.Sample();

        if (!svc.Available) return; // 无 GPU 或计数器不可用：跳过
        foreach (var kv in map)
        {
            Assert.True(kv.Key > 0, $"pid must be positive: {kv.Key}");
            Assert.InRange(kv.Value, 0, 100);
        }
    }
}
