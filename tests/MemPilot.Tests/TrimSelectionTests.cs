using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 旧的 `CleanupEngine.IsTrimmableSelection` 已在 v0.9.2 删除——它是最宽松的一份安全判定
/// （只查受保护/系统/安全/反作弊/驱动 + CPU），与另外 5 份漂移。这里把断言迁到
/// `TrimSelection.IsSafe`，验证收敛后语义只增不减。
/// </summary>
public class TrimSelectionTests
{
    private static readonly DateTime Now = DateTime.Now;
    private static readonly HashSet<int> NoAudio = new();

    private static ProcessInfo P(int pid, ProcessCategory cat, bool prot = false, bool sys = false, bool svc = false)
        => new()
        {
            Pid = pid, Name = "p" + pid, Category = cat,
            IsProtected = prot, IsSystemProcess = sys, IsService = svc,
            StartTime = Now.AddMinutes(-60),
            Memory = new MemoryMetrics { CpuPercent = 0.5, PrivateWorkingSetBytes = 100L * 1024 * 1024 }
        };

    [Fact]
    public void ProtectedOrSystemProcesses_AreNeverTrimmed()
    {
        Assert.False(TrimSelection.IsSafe(P(1, ProcessCategory.ThirdPartyApp, prot: true), 0, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(2, ProcessCategory.SystemService, sys: true), 0, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(3, ProcessCategory.SystemCritical), 0, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(4, ProcessCategory.ThirdPartyApp, svc: true), 0, NoAudio, Now));
    }

    [Fact]
    public void SecurityAntiCheatDriver_AreNeverTrimmed()
    {
        Assert.False(TrimSelection.IsSafe(P(5, ProcessCategory.SecuritySoftware), 0, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(6, ProcessCategory.AntiCheat), 0, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(P(7, ProcessCategory.Driver), 0, NoAudio, Now));
    }

    [Fact]
    public void SelfIsNeverTrimmed_OrdinaryAppsAre()
    {
        Assert.False(TrimSelection.IsSafe(P(8, ProcessCategory.ThirdPartyApp), 8, NoAudio, Now));
        Assert.True(TrimSelection.IsSafe(P(9, ProcessCategory.ThirdPartyApp), 0, NoAudio, Now));
        Assert.True(TrimSelection.IsSafe(P(10, ProcessCategory.Bloatware), 0, NoAudio, Now));
        Assert.True(TrimSelection.IsSafe(P(11, ProcessCategory.BackgroundService), 0, NoAudio, Now));
    }

    [Fact]
    public void BusyProcesses_AreSkipped_IdleOnesAreTrimmed()
    {
        var busy = P(12, ProcessCategory.DevTool);
        busy.Memory.CpuPercent = 20;
        Assert.False(TrimSelection.IsSafe(busy, 0, NoAudio, Now));

        var idle = P(13, ProcessCategory.DevTool);
        idle.Memory.CpuPercent = 0.5;
        idle.Workload = WorkloadKind.Other;   // 用途非 Work，才能测到 CPU 条件
        Assert.True(TrimSelection.IsSafe(idle, 0, NoAudio, Now));
    }

    [Fact]
    public void TrimWorkingSets_NoLongerFiltersInternally()
    {
        // v0.9.2：TrimWorkingSets 只保证"不压自己"，筛选责任在调用方（TrimSelection）
        var src = File.ReadAllText(Path.Combine(
            Directory.GetParent(TestTmp.Dir)!.FullName, "src", "MemPilot.Core", "Services", "CleanupEngine.cs"));
        Assert.DoesNotContain("IsTrimmableSelection", src);
        Assert.Contains("p.Pid == self", src);
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

    // ---- v0.23.0: 租约调用方自身及子树永不处置 ----
    // 放在这个文件是因为它属于"谁不能被处置"，与保护/系统/Unknown 同一层，
    // 不是租约私有的规则（LeaseHardPlanner 只是复用同一份判定）。

    [Fact]
    public void ExcludedPids_AreRejected_EvenWhenOtherwiseSafe()
    {
        var p = P(1, "worker.exe", ProcessCategory.ThirdPartyApp);

        Assert.True(TrimSelection.IsSafe(p, 0, NoAudio, Now));
        Assert.False(TrimSelection.IsSafe(p, 0, NoAudio, Now, new HashSet<int> { 1 }));
    }

    [Fact]
    public void BuildSafeTrimSet_OmitsExcludedPids()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "a.exe", ProcessCategory.ThirdPartyApp),
            P(2, "b.exe", ProcessCategory.ThirdPartyApp),
            P(3, "c.exe", ProcessCategory.ThirdPartyApp),
        };

        var set = TrimSelection.BuildSafeTrimSet(procs, 0, NoAudio, new HashSet<int> { 2 });

        Assert.Equal(new[] { 1, 3 }, set.Select(p => p.Pid).OrderBy(x => x).ToArray());
    }

    // ---- v0.25.0：排序从"工作集大小"改为"预计释放" ----

    [Fact]
    public void BuildSafeTrimSet_OrdersByExpectedRelease_NotRawWorkingSet()
    {
        // A：工作集大但历史释放率低（活跃代码，压完立刻读回）→ expected 1000×0.1 = 100
        // B：工作集小但释放率高（闲置 buffered 数据）        → expected 300×0.9 = 270
        // 旧排序会先压 A（白压），新排序先压 B。
        var procs = new List<ProcessInfo>
        {
            P(1, "big-but-sticky.exe", ProcessCategory.ThirdPartyApp),
            P(2, "small-but-yielding.exe", ProcessCategory.ThirdPartyApp),
        };
        procs[0].Memory.PrivateWorkingSetBytes = 1000L * 1024 * 1024;
        procs[1].Memory.PrivateWorkingSetBytes = 300L * 1024 * 1024;

        var set = TrimSelection.BuildSafeTrimSet(procs, 0, NoAudio, null,
            releaseRatio: n => n == "small-but-yielding.exe" ? 0.9 : 0.1);

        Assert.Equal(new[] { 2, 1 }, set.Select(p => p.Pid).ToArray());
    }

    [Fact]
    public void BuildSafeTrimSet_WithoutHistory_FallsBackToWorkingSetOrder()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "small.exe", ProcessCategory.ThirdPartyApp),
            P(2, "big.exe", ProcessCategory.ThirdPartyApp),
        };
        procs[0].Memory.PrivateWorkingSetBytes = 100L * 1024 * 1024;
        procs[1].Memory.PrivateWorkingSetBytes = 900L * 1024 * 1024;

        var set = TrimSelection.BuildSafeTrimSet(procs, 0, NoAudio);

        Assert.Equal(new[] { 2, 1 }, set.Select(p => p.Pid).ToArray());
    }
}
