using MemoryTool.Models;
using MemoryTool.Services;
using Xunit;

namespace MemoryTool.Tests;

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
}
