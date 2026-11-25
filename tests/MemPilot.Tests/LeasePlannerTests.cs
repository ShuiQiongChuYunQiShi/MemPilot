using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 租约预演（v0.23.0）。plan 的全部价值在"零动作 + 如实"：
/// 只用快照算、只报能算出来的数、把安全边界挡掉的进程数摊开给调用方看。
/// </summary>
public class LeasePlannerTests
{
    private static readonly DateTime Now = DateTime.Now;
    private static readonly HashSet<int> NoAudio = new();
    private const long Gb = 1L << 30;

    private static ProcessInfo P(int pid, string name, ProcessCategory cat = ProcessCategory.ThirdPartyApp,
        WorkloadKind wl = WorkloadKind.Other, long privateBytes = Gb, int ageMinutes = 60,
        int parentPid = 0, bool prot = false)
        => new()
        {
            Pid = pid, ParentPid = parentPid, Name = name,
            Category = cat, Workload = wl, IsProtected = prot,
            HasVisibleWindow = false,                    // 默认够闲：hard 才挑得中
            StartTime = Now.AddMinutes(-ageMinutes),
            Memory = new MemoryMetrics { PrivateBytes = privateBytes, CpuPercent = 0 }
        };

    [Fact]
    public void Soft_ProjectsCurrentOnly_AndNeverSuspends()
    {
        var procs = new List<ProcessInfo> { P(1, "idle.exe") };
        var plan = LeasePlanner.Build(procs, 0, NoAudio, Now, targetAvailableGb: 8, LeasePolicy.Soft,
            currentAvailableGb: 10, snapshotAgeSeconds: 2.5);

        Assert.True(plan.Feasible);
        Assert.Equal("current-only", plan.ProjectionBasis);
        Assert.Empty(plan.WouldSuspend);
        Assert.Equal(10, plan.ProjectedAvailableGb);
        Assert.Equal(0, plan.ShortfallGb);
        Assert.Equal(2.5, plan.SnapshotAgeSeconds);
    }

    [Fact]
    public void Soft_TargetAboveCurrent_ReportsShortfall()
    {
        var plan = LeasePlanner.Build(new List<ProcessInfo>(), 0, NoAudio, Now, 20, LeasePolicy.Soft,
            currentAvailableGb: 10, snapshotAgeSeconds: 1);

        Assert.False(plan.Feasible);
        Assert.Equal(10, plan.ShortfallGb);
    }

    [Fact]
    public void Hard_PicksIdleProcessesUntilShortfallIsCovered()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "big-a.exe", privateBytes: 2 * Gb),
            P(2, "big-b.exe", privateBytes: 2 * Gb),
            P(3, "big-c.exe", privateBytes: 2 * Gb),
        };
        var plan = LeasePlanner.Build(procs, 0, NoAudio, Now, 14, LeasePolicy.Hard,
            currentAvailableGb: 10, snapshotAgeSeconds: 1);

        Assert.Equal("hard-upper-bound", plan.ProjectionBasis);
        Assert.Equal(2, plan.WouldSuspend.Count);       // 缺口 4GB / 每个 2GB
        Assert.True(plan.Feasible);
        Assert.Equal(14, plan.ProjectedAvailableGb);
        Assert.Equal(0, plan.ShortfallGb);
    }

    [Fact]
    public void Hard_PrefersTheLargestProcess_SoFewerProcessesAreDisturbed()
    {
        // 一个 8GB 就能补 4GB 缺口 → 只动 1 个（"少动一个"优先于"多动两个小的"）
        var procs = new List<ProcessInfo>
        {
            P(1, "huge.exe", privateBytes: 8 * Gb),
            P(2, "small.exe", privateBytes: 2 * Gb),
        };
        var plan = LeasePlanner.Build(procs, 0, NoAudio, Now, 14, LeasePolicy.Hard,
            currentAvailableGb: 10, snapshotAgeSeconds: 1);

        var picked = Assert.Single(plan.WouldSuspend);
        Assert.Equal(1, picked.Pid);
    }

    [Fact]
    public void Hard_WhenCurrentAlreadyEnough_PlansNothing()
    {
        var procs = new List<ProcessInfo> { P(1, "idle.exe", privateBytes: 8 * Gb) };
        var plan = LeasePlanner.Build(procs, 0, NoAudio, Now, 8, LeasePolicy.Hard,
            currentAvailableGb: 10, snapshotAgeSeconds: 1);

        Assert.Empty(plan.WouldSuspend);                // 不用动任何人
        Assert.Equal("current-only", plan.ProjectionBasis);
        Assert.True(plan.Feasible);
    }

    [Fact]
    public void Hard_NeverPicksMoreThanTheCap()
    {
        // 目标远超现实：也只能挑 MaxSuspendCount 个（永不一次挂一片）
        var procs = Enumerable.Range(1, 20)
            .Select(i => P(i, $"p{i}.exe", privateBytes: 100 * 1024 * 1024)).ToList();
        var plan = LeasePlanner.Build(procs, 0, NoAudio, Now, 100, LeasePolicy.Hard,
            currentAvailableGb: 0, snapshotAgeSeconds: 1);

        Assert.Equal(LeaseHardPlanner.MaxSuspendCount, plan.WouldSuspend.Count);
        Assert.False(plan.Feasible);
        Assert.True(plan.ShortfallGb > 0);
    }

    [Fact]
    public void Excluded_CountsByReason_SoSafetyRulesAreVisible()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "unknown.exe", cat: ProcessCategory.Unknown),
            P(2, "system.exe", cat: ProcessCategory.SystemCritical),
            P(3, "work.exe", wl: WorkloadKind.Work),
            P(4, "idle.exe"),                            // 可处置 → 不计入
        };
        var plan = LeasePlanner.Build(procs, 0, NoAudio, Now, 4, LeasePolicy.Soft,
            currentAvailableGb: 10, snapshotAgeSeconds: 1);

        Assert.Equal(3, plan.Excluded.Total);
        Assert.Equal(2, plan.Excluded.ByReason["category"]);   // Unknown + SystemCritical
        Assert.Equal(1, plan.Excluded.ByReason["workload"]);
        Assert.False(plan.Excluded.ByReason.ContainsKey("caller"));
    }

    [Fact]
    public void Excluded_CallerSubtree_IsCountedAndNeverPlanned()
    {
        var procs = new List<ProcessInfo>
        {
            P(100, "agent.exe", privateBytes: 4 * Gb),
            P(101, "worker.py", privateBytes: 4 * Gb, parentPid: 100),
        };
        var excluded = ProcessTreeBuilder.BuildSubtreePids(procs, 100);

        var plan = LeasePlanner.Build(procs, 0, NoAudio, Now, 12, LeasePolicy.Hard,
            currentAvailableGb: 0, snapshotAgeSeconds: 1, excludedPids: excluded);

        Assert.Equal(2, plan.Excluded.ByReason["caller"]);
        Assert.Empty(plan.WouldSuspend);                 // 就算凑不够也不碰调用方
        Assert.False(plan.Feasible);
    }

    [Fact]
    public void NoSnapshot_UsesMinusOne_AndDoesNotThrow()
    {
        var plan = LeasePlanner.Build(null, 0, null, Now, 4, LeasePolicy.Hard,
            currentAvailableGb: 0, snapshotAgeSeconds: -1);

        Assert.Equal(-1, plan.SnapshotAgeSeconds);       // -1 = 还没有快照，不是"刚刚采过"
        Assert.Empty(plan.WouldSuspend);
        Assert.False(plan.Feasible);
    }
}
