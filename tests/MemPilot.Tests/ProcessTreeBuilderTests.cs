using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

public class ProcessTreeBuilderTests
{
    private static ProcessInfo Proc(int pid, int parent, long ws) => new()
    {
        Pid = pid,
        ParentPid = parent,
        Name = $"p{pid}",
        Memory = new MemoryMetrics { WorkingSetBytes = ws, PrivateWorkingSetBytes = ws }
    };

    [Fact]
    public void Build_GroupsChildrenUnderParents_ByWorkingSet()
    {
        // tree: 1(500MB) -> 2(100MB), 3(300MB); root 9(50MB)
        var procs = new List<ProcessInfo>
        {
            Proc(2, 1, 100 * 1024 * 1024),
            Proc(1, 0, 500 * 1024 * 1024),
            Proc(9, 0, 50 * 1024 * 1024),
            Proc(3, 1, 300 * 1024 * 1024),
        };
        var ordered = ProcessTreeBuilder.Build(procs, new HashSet<int>());

        Assert.Equal(new[] { 1, 3, 2, 9 }, ordered.Select(p => p.Pid)); // 1 first (bigger), kids by WS desc, then 9
        Assert.Equal(0, ordered[0].TreeDepth);
        Assert.Equal(1, ordered[1].TreeDepth);
        Assert.Equal(1, ordered[2].TreeDepth);
        Assert.Equal(0, ordered[3].TreeDepth);
        Assert.True(ordered[0].HasChildren);
        Assert.False(ordered[1].HasChildren);
    }

    [Fact]
    public void Build_Collapse_HidesDescendants()
    {
        var procs = new List<ProcessInfo>
        {
            Proc(1, 0, 500 * 1024 * 1024),
            Proc(2, 1, 100 * 1024 * 1024),
            Proc(3, 2, 50 * 1024 * 1024), // grandchild
        };
        var ordered = ProcessTreeBuilder.Build(procs, new HashSet<int> { 1 });
        Assert.Single(ordered); // only the collapsed root remains
        Assert.True(ordered[0].HasChildren);
    }

    [Fact]
    public void Build_OrphansBecomeRoots()
    {
        // parent pid 99 does not exist -> pid 1 is a root
        var procs = new List<ProcessInfo> { Proc(1, 99, 100 * 1024 * 1024) };
        var ordered = ProcessTreeBuilder.Build(procs, new HashSet<int>());
        var row = Assert.Single(ordered);
        Assert.Equal(0, row.TreeDepth);
    }

    [Fact]
    public void Build_CycleMembersSurfaceAsRoots()
    {
        // 1 -> 2 -> 1 cycle: neither has an in-set root parent; both must survive
        var procs = new List<ProcessInfo> { Proc(1, 2, 200 * 1024 * 1024), Proc(2, 1, 100 * 1024 * 1024) };
        var ordered = ProcessTreeBuilder.Build(procs, new HashSet<int>());
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void Build_DeadParentIdsArePruned()
    {
        var procs = new List<ProcessInfo> { Proc(1, 0, 100 * 1024 * 1024) };
        procs[0].ChildPids.Add(12345); // stale child id
        ProcessTreeBuilder.Build(procs, new HashSet<int>());
        Assert.Empty(procs[0].ChildPids);
    }

    // ---- BuildSubtreePids: v0.23.0 "caller and its subtree are never touched" ----

    [Fact]
    public void Subtree_IncludesSelfAndAllDescendants_ButNotSiblings()
    {
        // 100 -> 101 -> 102, 100 -> 103; unrelated 200 and its child 201
        var procs = new List<ProcessInfo>
        {
            Proc(100, 0, 1), Proc(101, 100, 1), Proc(102, 101, 1), Proc(103, 100, 1),
            Proc(200, 0, 1), Proc(201, 200, 1),
        };
        var set = ProcessTreeBuilder.BuildSubtreePids(procs, 100);
        Assert.Equal(new HashSet<int> { 100, 101, 102, 103 }, set);
    }

    [Fact]
    public void Subtree_IsOrderIndependent_AndCycleSafe()
    {
        // children listed before parents; 101 <-> 102 would loop if visited were not tracked
        var procs = new List<ProcessInfo>
        {
            Proc(102, 101, 1), Proc(101, 100, 1), Proc(100, 0, 1), Proc(103, 102, 1),
        };
        var set = ProcessTreeBuilder.BuildSubtreePids(procs, 100);
        Assert.Equal(new HashSet<int> { 100, 101, 102, 103 }, set);
    }

    [Fact]
    public void Subtree_RootOutsideSnapshot_StillExcludesThatPid()
    {
        // caller already exited: excluding a dead pid is harmless, and if the pid was
        // reused we must still leave it alone (safe direction)
        var set = ProcessTreeBuilder.BuildSubtreePids(new List<ProcessInfo> { Proc(7, 0, 1) }, 999);
        Assert.Equal(new HashSet<int> { 999 }, set);
    }

    [Fact]
    public void Subtree_NonPositiveRoot_IsEmpty()
    {
        Assert.Empty(ProcessTreeBuilder.BuildSubtreePids(new List<ProcessInfo> { Proc(1, 0, 1) }, 0));
        Assert.Empty(ProcessTreeBuilder.BuildSubtreePids(null!, 5));
    }
}
