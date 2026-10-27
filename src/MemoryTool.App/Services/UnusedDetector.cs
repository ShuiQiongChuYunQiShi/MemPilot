using MemoryTool.Models;

namespace MemoryTool.Services;

public class UnusedDetector
{
    public AppConfig Config { get; set; } = new();
    private readonly Dictionary<int, (DateTime t, TimeSpan cpu, long ioR, long ioW)> _prev = new();

    public List<UnusedCandidate> Detect(List<ProcessInfo> processes, SystemMemoryInfo mem, AppMode mode)
    {
        var candidates = new List<UnusedCandidate>();
        var now = DateTime.UtcNow;
        foreach (var p in processes)
        {
            if (p.IsProtected || p.IsSystemProcess || p.IsService) continue;
            if (p.IsStartupItem) continue;
            if (p.IsScheduledTask) continue;
            if (p.IsWhitelisted) continue;

            double cpuPct = 0;
            double ioRate = 0;
            if (_prev.TryGetValue(p.Pid, out var prev))
            {
                var dt = (now - prev.t).TotalSeconds;
                if (dt > 0)
                {
                    var cpuDelta = (p.Memory.TotalCpuTime - prev.cpu).TotalSeconds;
                    cpuPct = cpuDelta / dt * 100.0 / Environment.ProcessorCount;
                    var ioDelta = (p.Memory.IoReadBytes + p.Memory.IoWriteBytes) - (prev.ioR + prev.ioW);
                    ioRate = ioDelta / dt;
                    p.Memory.IoReadRateBytesPerSec = (p.Memory.IoReadBytes - prev.ioR) / dt;
                    p.Memory.IoWriteRateBytesPerSec = (p.Memory.IoWriteBytes - prev.ioW) / dt;
                }
            }
            p.Memory.CpuPercent = Math.Max(0, cpuPct);
            _prev[p.Pid] = (now, p.Memory.TotalCpuTime, p.Memory.IoReadBytes, p.Memory.IoWriteBytes);

            bool lowCpu = cpuPct < Config.UnusedCpuThresholdPercent;
            bool lowIo = ioRate < 4096;
            bool hasWindow = p.HasVisibleWindow;
            bool hasChildActivity = p.ChildPids.Count > 0;

            if (lowCpu && lowIo && !hasWindow && !hasChildActivity)
            {
                p.IdleMinutes += Math.Max(0.1, Config.RefreshIntervalSeconds / 60.0);
            }
            else
            {
                p.IdleMinutes = 0;
            }

            bool idleEnough = p.IdleMinutes >= Config.UnusedIdleMinutes;

            var reasons = new List<string>();
            bool whitelisted = Config.Whitelist.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || p.ExecutablePath.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (whitelisted) { p.IsWhitelisted = true; continue; }
            bool blacklisted = Config.Blacklist.Any(b => p.Name.Contains(b, StringComparison.OrdinalIgnoreCase));

            if (!hasWindow) reasons.Add("no visible window");
            if (lowCpu) reasons.Add($"CPU {cpuPct:F1}% < {Config.UnusedCpuThresholdPercent}%");
            if (lowIo) reasons.Add("low IO");
            if (idleEnough) reasons.Add($"idle {p.IdleMinutes:F1} min");
            if (!hasChildActivity) reasons.Add("no child process activity");
            if (!p.IsService) reasons.Add("not a service/scheduled task");
            if (blacklisted) reasons.Add("blacklisted");

            bool candidate = !hasWindow && lowCpu && lowIo && idleEnough && !hasChildActivity && !p.IsService && !p.IsStartupItem;
            if (blacklisted) candidate = true;

            if (candidate)
            {
                p.IsUnusedCandidate = true;
                long est = (long)(p.Memory.WorkingSetBytes * 0.7);
                p.EstimatedReclaimableBytes = est;
                var risk = ProcessRiskLevel.Low;
                if (p.Category == ProcessCategory.Unknown) risk = ProcessRiskLevel.Medium;
                var level = CleanupActionLevel.L1Trim;
                if (blacklisted) level = CleanupActionLevel.L3SuggestClose;
                candidates.Add(new UnusedCandidate { Process = p, Reasons = reasons, EstimatedReclaimableBytes = est, Risk = risk, SuggestedLevel = level });
            }
        }
        return candidates;
    }
}
