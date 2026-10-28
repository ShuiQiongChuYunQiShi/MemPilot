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
