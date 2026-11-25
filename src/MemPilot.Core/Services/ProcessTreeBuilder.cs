using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// Flattens a process list into a depth-first tree order: roots (processes whose parent
/// is not in the set) sorted by working set, children grouped under their parents.
/// Sets TreeDepth/HasChildren on each item; descendants of collapsed pids are omitted.
/// Robust against orphans and parent/child cycles (unreachables surface as roots).
/// </summary>
public static class ProcessTreeBuilder
{
    /// <summary>
    /// Collects <paramref name="rootPid"/> plus every descendant (multi-level, cycle-safe).
    ///
    /// v0.23.0 — lease hard invariant "never touch the caller or its process tree".
    /// An agent's worker is typically windowless and idle, which is exactly what
    /// <see cref="LeaseHardPlanner"/> looks for; without this exclusion a caller can
    /// suspend its own worker and then wait for it forever.
    ///
    /// Returns an empty set when rootPid is not positive. The root itself is always
    /// included even if absent from the snapshot: excluding a dead pid is harmless,
    /// and if that pid got reused by an unrelated process the safe direction is still
    /// "do not touch it".
    /// </summary>
    public static HashSet<int> BuildSubtreePids(IEnumerable<ProcessInfo> procs, int rootPid)
    {
        var result = new HashSet<int>();
        if (procs == null || rootPid <= 0) return result;

        // Parent links are not guaranteed to be in topological order, so index by parent
        // first and walk with a stack instead of a single pass.
        var byParent = new Dictionary<int, List<int>>();
        foreach (var p in procs)
        {
            if (p == null || p.ParentPid == 0 || p.ParentPid == p.Pid) continue;
            if (!byParent.TryGetValue(p.ParentPid, out var kids))
                byParent[p.ParentPid] = kids = new List<int>();
            kids.Add(p.Pid);
        }

        var stack = new Stack<int>();
        stack.Push(rootPid);
        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            if (!result.Add(pid)) continue;   // already seen: breaks cycles and repeats
            if (byParent.TryGetValue(pid, out var kids))
                foreach (var k in kids) stack.Push(k);
        }
        return result;
    }

    public static List<ProcessInfo> Build(IEnumerable<ProcessInfo> procs, ISet<int> collapsedPids)
    {
        var list = procs.ToList();
        var byId = new Dictionary<int, ProcessInfo>(list.Count);
        foreach (var p in list) byId[p.Pid] = p;

        var children = new Dictionary<int, List<ProcessInfo>>();
        var roots = new List<ProcessInfo>();
        foreach (var p in list)
        {
            p.HasChildren = false;
            p.ChildPids.RemoveAll(c => !byId.ContainsKey(c) || c == p.Pid); // drop stale references
            if (p.ParentPid != 0 && p.ParentPid != p.Pid &&
                byId.TryGetValue(p.ParentPid, out var parent) && parent != p)
            {
                if (!children.TryGetValue(p.ParentPid, out var kids))
                    children[p.ParentPid] = kids = new List<ProcessInfo>();
                kids.Add(p);
            }
            else
            {
                roots.Add(p);
            }
        }

        var emitted = new HashSet<ProcessInfo>();
        var suppressed = new HashSet<ProcessInfo>(); // hidden by collapse — must not resurface
        var result = new List<ProcessInfo>(list.Count);

        void CollectSuppressed(ProcessInfo p)
        {
            if (children.TryGetValue(p.Pid, out var kids))
                foreach (var kid in kids)
                    if (suppressed.Add(kid)) CollectSuppressed(kid);
        }

        void Emit(ProcessInfo p, int depth)
        {
            p.TreeDepth = depth;
            p.HasChildren = children.ContainsKey(p.Pid);
            p.Collapsed = collapsedPids.Contains(p.Pid);
            emitted.Add(p);
            result.Add(p);
            if (collapsedPids.Contains(p.Pid))
            {
                CollectSuppressed(p);
                return;
            }
            if (children.TryGetValue(p.Pid, out var kids))
                foreach (var kid in kids.OrderByDescending(k => k.Memory.WorkingSetBytes))
                    if (emitted.Add(kid) && !suppressed.Contains(kid)) Emit(kid, depth + 1);
        }

        foreach (var r in roots.OrderByDescending(p => p.Memory.WorkingSetBytes))
            if (emitted.Add(r) && !suppressed.Contains(r)) Emit(r, 0);

        // cycle members are unreachable from any root — surface them as roots too
        foreach (var p in list.OrderByDescending(p => p.Memory.WorkingSetBytes))
            if (emitted.Add(p) && !suppressed.Contains(p)) Emit(p, 0);

        return result;
    }
}
