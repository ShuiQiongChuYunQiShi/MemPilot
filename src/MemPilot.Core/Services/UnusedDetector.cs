using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// Detects unused processes. State (idle streaks, previous CPU/IO samples) is kept per
/// (pid, startTime) across refreshes — PID reuse is defeated by the start time.
/// Candidacy requires: minimum process age, a streak of consecutive idle observations,
/// an idle-duration threshold, no visible window, no active audio session, no children,
/// and a known non-protected category. Unknown categories are never auto-disposed.
/// </summary>
public class UnusedDetector
{
    public AppConfig Config { get; set; } = new();

    private class IdleState
    {
        public DateTime FirstSeenUtc;
        public DateTime LastActiveUtc;
        public DateTime LastSeenUtc;
        public int ConsecutiveIdleObs;
        public TimeSpan PrevCpu;
        public long PrevIo;
        public List<(DateTime t, long ws)> WsHistory = new();  // ring buffer for growth watch
    }

    private readonly Dictionary<(int pid, DateTime start), IdleState> _states = new();

    public List<UnusedCandidate> Detect(List<ProcessInfo> processes, SystemMemoryInfo mem,
        int foregroundPid, HashSet<int> audioPids, HashSet<int> suspendedPids)
    {
        var candidates = new List<UnusedCandidate>();
        var now = DateTime.UtcNow;
        var runningNames = new HashSet<string>(processes.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var p in processes)
        {
            // CPU/IO rates are computed for every process, not only candidates,
            // so the process tab shows real numbers for protected/system rows too.
            var key = p.StateKey;
            if (!_states.TryGetValue(key, out var st))
            {
                st = new IdleState { FirstSeenUtc = now, LastActiveUtc = now, LastSeenUtc = now };
                _states[key] = st;
            }
            // compute rates against the PREVIOUS timestamp — updating LastSeenUtc first
            // would make dt always zero and every busy headless process look idle.
            var dt = (now - st.LastSeenUtc).TotalSeconds;
            st.LastSeenUtc = now;

            double cpuPct = 0;
            double ioRate = 0;
            if (dt > 0)
            {
                var cpuDelta = (p.Memory.TotalCpuTime - st.PrevCpu).TotalSeconds;
                if (cpuDelta > 0) cpuPct = cpuDelta / dt * 100.0 / Environment.ProcessorCount;
                var ioDelta = (p.Memory.IoReadBytes + p.Memory.IoWriteBytes) - st.PrevIo;
                if (ioDelta > 0)
                {
                    ioRate = ioDelta / dt;
                    p.Memory.IoReadRateBytesPerSec = ioRate;
                }
            }
            p.Memory.CpuPercent = Math.Max(0, cpuPct);
            st.PrevCpu = p.Memory.TotalCpuTime;
            st.PrevIo = p.Memory.IoReadBytes + p.Memory.IoWriteBytes;

            // working-set history for the growth watch (leak-ish behavior, display only)
            st.WsHistory.Add((now, p.Memory.WorkingSetBytes));
            if (st.WsHistory.Count > 60) st.WsHistory.RemoveAt(0);
            if (st.WsHistory.Count >= 2)
                p.MemoryGrowthBytes = st.WsHistory[^1].ws - st.WsHistory[0].ws;
            else
                p.MemoryGrowthBytes = 0;

            p.AudioActive = audioPids.Contains(p.Pid);
            p.IsSuspended = suspendedPids.Contains(p.Pid);

            bool lowCpu = cpuPct < Config.UnusedCpuThresholdPercent;
            bool lowIo = ioRate < 4096;
            bool hasWindow = p.HasVisibleWindow;
            bool hasChildren = p.ChildPids.Count > 0;
            bool isIdle = lowCpu && lowIo && !hasWindow && !hasChildren && !p.AudioActive && p.Pid != foregroundPid;

            if (isIdle)
            {
                st.ConsecutiveIdleObs++;
            }
            else
            {
                st.ConsecutiveIdleObs = 0;
                st.LastActiveUtc = now;
            }
            p.IdleMinutes = Math.Max(0, (now - st.LastActiveUtc).TotalMinutes);

            // whitelists win over everything
            bool whitelisted = Config.Whitelist.Any(w =>
                p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                p.ExecutablePath.Contains(w, StringComparison.OrdinalIgnoreCase));
            p.IsWhitelisted |= whitelisted;
            if (whitelisted) continue;

            bool blacklisted = Config.Blacklist.Any(b => p.Name.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                                                          p.Name.Contains(b, StringComparison.OrdinalIgnoreCase));
            p.IsBlacklisted = blacklisted;

            // hard exclusions: system-facing, mode-keep or trust-unknown processes are never candidates
            if (p.IsProtected || p.IsSystemProcess || p.IsService || p.IsStartupItem || p.IsScheduledTask || p.ModeKeep) continue;
            if (p.Category == ProcessCategory.Unknown || p.Category == ProcessCategory.SystemCritical ||
                p.Category == ProcessCategory.SecuritySoftware || p.Category == ProcessCategory.AntiCheat ||
                p.Category == ProcessCategory.Driver) continue;

            double ageSec = p.StartTime == DateTime.MinValue ? double.MaxValue : (DateTime.Now - p.StartTime).TotalSeconds;
            if (ageSec < Config.MinProcessAgeSeconds) continue;

            var reasons = new List<string>();
            if (!hasWindow) reasons.Add("无前台窗口");
            if (lowCpu) reasons.Add($"CPU {cpuPct:F1}% < {Config.UnusedCpuThresholdPercent}%");
            if (lowIo) reasons.Add("低 IO");
            if (!p.AudioActive) reasons.Add("无音频活动");
            if (p.IdleMinutes >= Config.UnusedIdleMinutes) reasons.Add($"空闲 {p.IdleMinutes:F0} 分钟");
            if (!hasChildren) reasons.Add("无子进程");
            var orphanHost = ResidualDetector.FindOrphanHost(p.Name, runningNames);
            if (orphanHost != null) reasons.Add($"残留服务（宿主 {orphanHost} 已退出）");

            bool regularCandidate = st.ConsecutiveIdleObs >= Config.RequiredIdleObservations &&
                                    p.IdleMinutes >= Config.UnusedIdleMinutes;
            // blacklisted processes only need a short observation grace (2 refreshes) — but never zero
            bool blacklistCandidate = blacklisted && st.ConsecutiveIdleObs >= 2;

            if ((regularCandidate && isIdle) || (blacklistCandidate && isIdle))
            {
                p.IsUnusedCandidate = true;
                // honest estimates: trim only yields the private working set; close/kill yields it too
                long phys = p.Memory.PrivateWorkingSetBytes;
                long commit = p.Memory.PrivateBytes;
                p.EstimatedReclaimableBytes = phys;
                var risk = blacklisted ? ProcessRiskLevel.Medium
                         : p.Category == ProcessCategory.Game || p.Category == ProcessCategory.GameLauncher ? ProcessRiskLevel.Medium
                         : ProcessRiskLevel.Low;
                var level = blacklisted
                    ? (hasWindow ? CleanupActionLevel.L3GracefulClose : CleanupActionLevel.L4ForceKill)
                    : CleanupActionLevel.L1Trim;
                if (p.IsSuspended) level = CleanupActionLevel.L2Suspend;
                candidates.Add(new UnusedCandidate
                {
                    Process = p,
                    Reasons = reasons,
                    EstimatedReclaimableBytes = phys,
                    EstimatedCommitReclaimableBytes = commit,
                    Risk = risk,
                    SuggestedLevel = level
                });
            }
        }

        // prune state for dead processes
        if (_states.Count > 8192)
        {
            var dead = _states.Where(kv => (now - kv.Value.LastSeenUtc).TotalMinutes > 10).Select(kv => kv.Key).ToList();
            foreach (var k in dead) _states.Remove(k);
        }
        return candidates;
    }
}
