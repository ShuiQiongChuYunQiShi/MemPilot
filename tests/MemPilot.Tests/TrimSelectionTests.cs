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

/// <summary>
/// v0.5.0 安全子集：提交内存告急时的自动压缩只允许碰"低负载后台"。
/// 用户硬约束是"绝不回收工作进程"，这组测试就是那条约束的可执行版本。
/// </summary>
public class SafeTrimSelectionTests
{
    private static readonly DateTime Now = DateTime.Now;
    private static readonly HashSet<int> NoAudio = new();

    private static ProcessInfo P(int pid, string name, ProcessCategory cat, WorkloadKind wl = WorkloadKind.Other,
        double cpu = 0, long io = 0, bool audio = false, int ageMinutes = 60,
        bool isProtected = false, bool isService = false, bool whitelisted = false, bool modeKeep = false, bool suspended = false)
        => new()
        {
            Pid = pid,
            Name = name,
            Category = cat,
            Workload = wl,
            AudioActive = audio,
            IsProtected = isProtected,
            IsService = isService,
            IsWhitelisted = whitelisted,
            ModeKeep = modeKeep,
            IsSuspended = suspended,
            StartTime = Now.AddMinutes(-ageMinutes),
            Memory = new MemoryMetrics
            {
                CpuPercent = cpu,
                IoReadRateBytesPerSec = io,
                PrivateWorkingSetBytes = 100L * 1024 * 1024
            }
        };

    [Fact]
    public void IdleBackgroundApp_IsIncluded()
        => Assert.True(TrimSelection.IsSafe(P(1, "updater.exe", ProcessCategory.ThirdPartyApp), 99, NoAudio, Now));

    [Theory]
    [InlineData(WorkloadKind.Work)]
    [InlineData(WorkloadKind.Ai)]
    [InlineData(WorkloadKind.Entertainment)]
    public void WorkingProcesses_AreExcludedByWorkload(WorkloadKind wl)
        => Assert.False(TrimSelection.IsSafe(P(1, "worker.exe", ProcessCategory.ThirdPartyApp, wl), 99, NoAudio, Now));

    [Theory]
    [InlineData(ProcessCategory.SystemCritical)]
    [InlineData(ProcessCategory.SystemService)]
    [InlineData(ProcessCategory.Driver)]
    [InlineData(ProcessCategory.SecuritySoftware)]
    [InlineData(ProcessCategory.AntiCheat)]
    [InlineData(ProcessCategory.Game)]
    [InlineData(ProcessCategory.GameLauncher)]
    [InlineData(ProcessCategory.AiImageVideo)]
    [InlineData(ProcessCategory.Unknown)]
    public void SensitiveCategories_AreExcluded(ProcessCategory cat)
        => Assert.False(TrimSelection.IsSafe(P(1, "x.exe", cat), 99, NoAudio, Now));

    [Fact]
    public void ProtectedServiceWhitelistedSuspendedModeKeep_AreExcluded()
    {
        Assert.False(TrimSelection.IsSafe(P(1, "a", ProcessCategory.ThirdPartyApp, isProtected: true), 99, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(1, "a", ProcessCategory.ThirdPartyApp, isService: true), 99, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(1, "a", ProcessCategory.ThirdPartyApp, whitelisted: true), 99, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(1, "a", ProcessCategory.ThirdPartyApp, suspended: true), 99, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(1, "a", ProcessCategory.ThirdPartyApp, modeKeep: true), 99, NoAudio, Now));
    }

    [Fact]
    public void ForegroundAudioBusyOrYoung_AreExcluded()
    {
        Assert.False(TrimSelection.IsSafe(P(1, "a", ProcessCategory.ThirdPartyApp), 1, NoAudio, Now));                         // 前台
        Assert.False(TrimSelection.IsSafe(P(2, "a", ProcessCategory.ThirdPartyApp, audio: true), 99, NoAudio, Now));           // 发声
        Assert.False(TrimSelection.IsSafe(P(3, "a", ProcessCategory.ThirdPartyApp), 99, new HashSet<int> { 3 }, Now));         // 音频会话
        Assert.False(TrimSelection.IsSafe(P(4, "a", ProcessCategory.ThirdPartyApp, cpu: 8), 99, NoAudio, Now));                // CPU 忙
        Assert.False(TrimSelection.IsSafe(P(5, "a", ProcessCategory.ThirdPartyApp, io: 2L * 1024 * 1024), 99, NoAudio, Now));  // IO 忙
        Assert.False(TrimSelection.IsSafe(P(6, "a", ProcessCategory.ThirdPartyApp, ageMinutes: 2), 99, NoAudio, Now));         // 刚启动
    }

    [Fact]
    public void BuildSafeTrimSet_ReturnsOnlyIdleOnes_SortedBySize()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "idle-a.exe", ProcessCategory.ThirdPartyApp),
            P(2, "working.exe", ProcessCategory.ThirdPartyApp, WorkloadKind.Work),
            P(3, "game.exe", ProcessCategory.Game, WorkloadKind.Entertainment),
            P(4, "idle-b.exe", ProcessCategory.ThirdPartyApp),
        };
        procs[0].Memory.PrivateWorkingSetBytes = 200L * 1024 * 1024;
        procs[3].Memory.PrivateWorkingSetBytes = 500L * 1024 * 1024;

        var set = TrimSelection.BuildSafeTrimSet(procs, 99, NoAudio);
        Assert.Equal(new[] { 4, 1 }, set.Select(p => p.Pid).ToArray());
    }

    [Fact]
    public void EmptyInput_IsSafe()
    {
        Assert.Empty(TrimSelection.BuildSafeTrimSet(Array.Empty<ProcessInfo>(), 0, NoAudio));
        Assert.Empty(TrimSelection.BuildSafeTrimSet(null!, 0, NoAudio));
    }
}
