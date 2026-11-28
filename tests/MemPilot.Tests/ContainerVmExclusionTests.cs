using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.22.0：容器/VM 宿主（vmmemWSL）不是"闲置进程"。
/// 压缩工作集会把整个 WSL 换出到磁盘（下次编译/跑模型先卡几秒读回）；
/// make-room 的 L4 更是直接杀掉内存宿主。三处动作入口必须一致排除 ContainerVm。
/// </summary>
public class ContainerVmExclusionTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static ProcessInfo Vmmem() => new()
    {
        Pid = 4242,
        Name = "vmmemWSL",
        Category = ProcessCategory.ContainerVm,
        StartTime = DateTime.Now - TimeSpan.FromHours(10),
        Memory = new MemoryMetrics
        {
            PrivateBytes = 4 * Gb,
            PrivateWorkingSetBytes = 4 * Gb,
            WorkingSetBytes = 4 * Gb,
        }
    };

    private static ProcessInfo OrdinaryIdle() => new()
    {
        Pid = 7777,
        Name = "notepad-idle",
        Category = ProcessCategory.ThirdPartyApp,
        StartTime = DateTime.Now - TimeSpan.FromHours(2),
        Memory = new MemoryMetrics { PrivateWorkingSetBytes = 200L * 1024 * 1024 }
    };

    private static SystemMemoryInfo LowMemory() => new()
    {
        AvailablePhysicalBytes = 1 * Gb,
        TotalCommitLimitBytes = 32 * Gb,
        TotalCommittedBytes = 31 * Gb,
    };

    [Fact]
    public void TrimSelection_RejectsContainerVm()
        => Assert.False(TrimSelection.IsSafe(Vmmem(), -1, new HashSet<int>(), DateTime.Now));

    [Fact]
    public void TrimSelection_StillAllowsOrdinaryIdleProcess()
        => Assert.True(TrimSelection.IsSafe(OrdinaryIdle(), -1, new HashSet<int>(), DateTime.Now));

    [Fact]
    public void SafeTrimSet_NeverContainsContainerVm()
    {
        var set = TrimSelection.BuildSafeTrimSet(
            new List<ProcessInfo> { Vmmem(), OrdinaryIdle() }, -1, new HashSet<int>());

        Assert.DoesNotContain(set, p => p.Category == ProcessCategory.ContainerVm);
        Assert.Contains(set, p => p.Name == "notepad-idle");
    }

    [Fact]
    public void UnusedDetector_RejectsContainerVm()
    {
        var detector = new UnusedDetector();
        var procs = new List<ProcessInfo> { Vmmem() };
        List<UnusedCandidate> candidates = new();

        // 跑满观察窗口（默认 6 轮）再断言：即便"连续空闲"成立也不能成为候选
        for (int i = 0; i < 10; i++)
            candidates = detector.Detect(procs, LowMemory(), -1, new HashSet<int>(), new HashSet<int>());

        Assert.DoesNotContain(candidates, c => c.Process.Name == "vmmemWSL");
    }

    [Fact]
    public void MakeRoom_NeverPicksContainerVm_EvenWhenKillAllowed()
    {
        var engine = new CleanupEngine(new ProcessService(), new AuditLogger(Path.Combine(TestTmp.Dir, "vm-" + Guid.NewGuid().ToString("N"))),
            Path.Combine(TestTmp.Dir, "vm-" + Guid.NewGuid().ToString("N")));
        var profile = new ModeProfile { Mode = AppMode.Balanced, Name = "Balanced" };
        var req = new MakeRoomRequest { AllowKill = true, IncludeTrim = true, TargetAvailableGB = 8 };

        var plan = engine.BuildPlan(new List<ProcessInfo> { Vmmem() }, LowMemory(), profile, req, -1, new HashSet<int>());

        Assert.DoesNotContain(plan.Items, i => i.Process.Name == "vmmemWSL");
    }
}
