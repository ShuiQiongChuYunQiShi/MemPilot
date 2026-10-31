using MemPilot.Helpers;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 显存读取（v0.3.4，PDH）：环境容忍型测试——有 GPU 就断言语义，没有就把诊断落盘。
/// 关键是"不可用 ≠ 0"：读不到时 UsedBytes 必须为 null。
/// </summary>
public class GpuMemoryServiceTests
{
    [Fact]
    public void Sample_RecordsDiagnostics_AndKeepsUnavailableAsNull()
    {
        using var svc = new GpuMemoryService();
        var snap = svc.Sample();

        var lines = new List<string>
        {
            $"procAvailable={snap.ProcessDedicatedAvailable}",
            $"adapterAvailable={snap.AdapterAvailable}",
            $"source={snap.Source}",
            $"used={(snap.UsedBytes?.ToString() ?? "null")}",
            $"procCount={snap.ProcessDedicated.Count}",
            $"procSum={snap.ProcessDedicatedSum}",
            $"adapterSum={snap.AdapterDedicatedBytes}",
            $"diag={svc.LastDiag}"
        };
        File.WriteAllLines(Path.Combine(TestTmp.Dir, "gpu-mem-diag.txt"), lines);

        if (!snap.Available)
        {
            Assert.Null(snap.UsedBytes);      // 不可用绝不返回 0
            Assert.Equal("none", snap.Source);
            return;
        }

        Assert.NotNull(snap.UsedBytes);
        Assert.True(snap.UsedBytes >= 0);
        Assert.All(snap.ProcessDedicated.Values, v => Assert.True(v >= 0));
        if (snap.AdapterAvailable) Assert.True(snap.AdapterDedicatedBytes >= 0);
        // 回归守卫：PDH_FMT_LARGE 写成 PDH_FMT_LONG 时 16 GB 会截成 2^31-1，看起来像"整卡满"
        Assert.NotEqual(int.MaxValue, snap.AdapterDedicatedBytes);
        Assert.DoesNotContain(snap.ProcessDedicated.Values, v => v == int.MaxValue);
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000ABCD_phys_0", 1234)]
    [InlineData("pid_42_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 42)]
    [InlineData("luid_0x00000000_0x000143E6_phys_0", 0)]  // 适配器实例没有 pid
    [InlineData("", 0)]
    public void ParsePid_HandlesBothInstanceShapes(string instance, int expect)
        => Assert.Equal(expect, PdhQuery.ParsePid(instance));
}
