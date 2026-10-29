using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>"压缩而不杀"的批量选择规则：受保护/系统/安全/反作弊/驱动/自身一律跳过。</summary>
public class TrimSelectionTests
{
    private static ProcessInfo P(int pid, ProcessCategory cat, bool prot = false, bool sys = false)
        => new() { Pid = pid, Name = "p" + pid, Category = cat, IsProtected = prot, IsSystemProcess = sys };

    [Fact]
    public void ProtectedOrSystemProcesses_AreNeverTrimmed()
    {
        Assert.False(CleanupEngine.IsTrimmableSelection(P(1, ProcessCategory.ThirdPartyApp, prot: true), 0));
        Assert.False(CleanupEngine.IsTrimmableSelection(P(2, ProcessCategory.SystemService, sys: true), 0));
        Assert.False(CleanupEngine.IsTrimmableSelection(P(3, ProcessCategory.SystemCritical), 0));
    }

    [Fact]
    public void SecurityAntiCheatDriver_AreNeverTrimmed()
    {
        Assert.False(CleanupEngine.IsTrimmableSelection(P(4, ProcessCategory.SecuritySoftware), 0));
        Assert.False(CleanupEngine.IsTrimmableSelection(P(5, ProcessCategory.AntiCheat), 0));
        Assert.False(CleanupEngine.IsTrimmableSelection(P(6, ProcessCategory.Driver), 0));
    }

    [Fact]
    public void SelfIsNeverTrimmed_OrdinaryAppsAre()
    {
        Assert.False(CleanupEngine.IsTrimmableSelection(P(7, ProcessCategory.ThirdPartyApp), 7));
        Assert.True(CleanupEngine.IsTrimmableSelection(P(8, ProcessCategory.ThirdPartyApp), 0));
        Assert.True(CleanupEngine.IsTrimmableSelection(P(9, ProcessCategory.Bloatware), 0));
        Assert.True(CleanupEngine.IsTrimmableSelection(P(10, ProcessCategory.BackgroundService), 0));
    }

    // 活性守卫：正在干活的进程不压（否则立刻缺页回填，表现为明显抖动）
    [Fact]
    public void BusyProcesses_AreSkipped_IdleOnesAreTrimmed()
    {
        var busy = P(11, ProcessCategory.DevTool);
        busy.Memory.CpuPercent = 20;
        Assert.False(CleanupEngine.IsTrimmableSelection(busy, 0));

        var idle = P(12, ProcessCategory.DevTool);
        idle.Memory.CpuPercent = 0.5;
        Assert.True(CleanupEngine.IsTrimmableSelection(idle, 0));
    }
}
