using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 硬租约的挂起选择（v0.8.0）。用户硬约束仍是"不碰工作进程"，硬租约比软租约多一道"必须足够闲"：
/// 无窗口 或 已空闲 ≥10 分钟；按私有提交降序挑，按缺口截断，最多 8 个。
/// </summary>
public class LeaseHardPlannerTests
{
    private static readonly DateTime Now = DateTime.Now;
    private static readonly HashSet<int> NoAudio = new();

    private static ProcessInfo P(int pid, string name, ProcessCategory cat = ProcessCategory.ThirdPartyApp,
        WorkloadKind wl = WorkloadKind.Other, bool window = false, double idle = 0,
        long privateBytes = 300L * 1024 * 1024, double cpu = 0, int ageMinutes = 60,
        bool isProtected = false, bool audio = false, int parentPid = 0)
        => new()
        {
            Pid = pid,
            ParentPid = parentPid,
            Name = name,
            Category = cat,
            Workload = wl,
            HasVisibleWindow = window,
            IdleMinutes = idle,
            AudioActive = audio,
            IsProtected = isProtected,
            StartTime = Now.AddMinutes(-ageMinutes),
            Memory = new MemoryMetrics
            {
                CpuPercent = cpu,
                PrivateBytes = privateBytes,
                PrivateWorkingSetBytes = privateBytes / 2
            }
        };

    [Fact]
    public void NoWindow_Idle_bothQualify()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "no-window.exe"),                     // 无窗口 → 够闲
            P(2, "idle-long.exe", idle: 30),           // 有窗口但空闲 30 分钟 → 够闲
        };
        var picked = LeaseHardPlanner.PickSuspendCandidates(procs, 99, NoAudio, Now);
        Assert.Equal(new[] { 1, 2 }, picked.Select(p => p.Pid).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void VisibleRecentlyUsed_IsExcluded()
    {
        var procs = new List<ProcessInfo> { P(1, "visible.exe", window: true, idle: 2) };
        Assert.Empty(LeaseHardPlanner.PickSuspendCandidates(procs, 99, NoAudio, Now));
    }

    [Theory]
    [InlineData(WorkloadKind.Work)]
    [InlineData(WorkloadKind.Ai)]
    [InlineData(WorkloadKind.Entertainment)]
    public void WorkWorkloads_AreNeverSuspended(WorkloadKind wl)
    {
        var procs = new List<ProcessInfo> { P(1, "worker.exe", wl: wl) };
        Assert.Empty(LeaseHardPlanner.PickSuspendCandidates(procs, 99, NoAudio, Now));
    }

    [Theory]
    [InlineData(ProcessCategory.Game)]
    [InlineData(ProcessCategory.AiImageVideo)]
    [InlineData(ProcessCategory.SystemService)]
    [InlineData(ProcessCategory.Unknown)]
    public void SensitiveCategories_AreNeverSuspended(ProcessCategory cat)
    {
        var procs = new List<ProcessInfo> { P(1, "x.exe", cat: cat) };
        Assert.Empty(LeaseHardPlanner.PickSuspendCandidates(procs, 99, NoAudio, Now));
    }

    [Fact]
    public void ForegroundAudioProtectedBusy_AreExcluded()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "fg.exe"), P(2, "audio.exe", audio: true), P(3, "prot.exe", isProtected: true),
            P(4, "busy.exe", cpu: 20),
        };
        var picked = LeaseHardPlanner.PickSuspendCandidates(procs, foregroundPid: 1, NoAudio, Now);
        Assert.Empty(picked);
    }

    [Fact]
    public void OrderedByPrivateCommitDescending()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "small.exe", privateBytes: 100L * 1024 * 1024),
            P(2, "large.exe", privateBytes: 900L * 1024 * 1024),
            P(3, "medium.exe", privateBytes: 400L * 1024 * 1024),
        };
        var picked = LeaseHardPlanner.PickSuspendCandidates(procs, 99, NoAudio, Now);
        Assert.Equal(new[] { 2, 3, 1 }, picked.Select(p => p.Pid).ToArray());
    }

    [Fact]
    public void TakeForShortfall_StopsWhenGoalReached()
    {
        var candidates = new List<ProcessInfo>
        {
            P(1, "a.exe", privateBytes: 1L << 30),   // 1GB
            P(2, "b.exe", privateBytes: 1L << 30),
            P(3, "c.exe", privateBytes: 1L << 30),
        };
        var picked = LeaseHardPlanner.TakeForShortfall(candidates, needBytes: (long)(1.5 * (1L << 30)));
        Assert.Equal(2, picked.Count);              // 1GB 不够，2GB 够了就停
    }

    [Fact]
    public void TakeForShortfall_RespectsMaxCount()
    {
        var candidates = Enumerable.Range(1, 20)
            .Select(i => P(i, $"p{i}.exe", privateBytes: 10L * 1024 * 1024)).ToList();
        var picked = LeaseHardPlanner.TakeForShortfall(candidates, needBytes: 10L << 30, maxCount: 8);
        Assert.Equal(8, picked.Count);              // 永不一次挂一片
    }

    [Fact]
    public void ZeroNeed_OrEmptyCandidates_IsSafe()
    {
        Assert.Empty(LeaseHardPlanner.TakeForShortfall(new List<ProcessInfo> { P(1, "a.exe") }, 0));
        Assert.Empty(LeaseHardPlanner.TakeForShortfall(null!, 1000));
        Assert.Empty(LeaseHardPlanner.PickSuspendCandidates(null!, 0, NoAudio, Now));
    }

    // ---- v0.23.0 hard invariant: the caller and its subtree are never suspended ----

    [Fact]
    public void RequesterSubtree_IsNeverPicked_EvenWhenPerfectlyIdle()
    {
        // 100 = the agent (node), 101 = its python worker. Both are windowless and idle,
        // i.e. they would otherwise be the *best* candidates. 200 is unrelated background.
        var procs = new List<ProcessInfo>
        {
            P(100, "node.exe", parentPid: 0),
            P(101, "worker.py", parentPid: 100),
            P(200, "other-idle.exe", parentPid: 0),
        };
        var excluded = ProcessTreeBuilder.BuildSubtreePids(procs, 100);

        var picked = LeaseHardPlanner.PickSuspendCandidates(procs, 99, NoAudio, Now, excluded);

        Assert.Equal(new[] { 200 }, picked.Select(p => p.Pid).ToArray());
    }

    [Fact]
    public void RequesterSubtree_DoesNotChangeBehaviourWhenNotProvided()
    {
        var procs = new List<ProcessInfo>
        {
            P(100, "node.exe"),
            P(200, "other-idle.exe", privateBytes: 900L * 1024 * 1024),
        };
        var picked = LeaseHardPlanner.PickSuspendCandidates(procs, 99, NoAudio, Now);
        Assert.Equal(new[] { 200, 100 }, picked.Select(p => p.Pid).ToArray());
    }
}
