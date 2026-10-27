using System.IO;
using System.Text.Json;
using MemoryTool.Models;

namespace MemoryTool.Services;

public class UsageTopProc
{
    public string N { get; set; } = "";     // process name
    public long W { get; set; }             // private working set bytes
    public double C { get; set; }           // cpu percent
    public int T { get; set; }              // thread count
}

/// <summary>One 5-minute snapshot of system shape: memory, process/thread counts,
/// active mode and the top memory consumers. The raw material for usage-habit analysis.</summary>
public class UsageSample
{
    public DateTime T { get; set; }
    public long Total { get; set; }
    public long Avail { get; set; }
    public long Commit { get; set; }
    public long CommitLimit { get; set; }
    public int Procs { get; set; }
    public int Threads { get; set; }
    public string Mode { get; set; } = "";
    public int Interval { get; set; }       // engine tick seconds at sampling time
    public List<UsageTopProc> Top { get; set; } = new();
}

/// <summary>
/// Persists usage habits as JSONL (one line per sample, one file per day, 5-minute
/// cadence — ~288 lines/day, tiny). Retention prunes files older than the configured
/// number of days. This data is the foundation for future AI features: habit-driven
/// make-room predictions and anomaly detection.
/// </summary>
public class UsageRecorder
{
    private readonly string _dir;
    private readonly int _retentionDays;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public UsageRecorder(string dataDir, int retentionDays = 30)
    {
        _dir = Path.Combine(dataDir, "history");
        _retentionDays = retentionDays;
        try { Directory.CreateDirectory(_dir); } catch { }
        Prune();
    }

    private static string FilePath(DateTime utc) => Path.Combine("history", $"samples-{utc:yyyyMMdd}.jsonl");

    /// <summary>Called every engine tick; writes at most one sample per 5 minutes.</summary>
    public void RecordSample(SystemMemoryInfo mem, List<ProcessInfo> procs, AppMode mode, int intervalSeconds)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastWriteUtc).TotalMinutes < 5) return;
        _lastWriteUtc = now;

        try
        {
            Directory.CreateDirectory(_dir);
            var sample = new UsageSample
            {
                T = now,
                Total = mem.TotalPhysicalBytes,
                Avail = mem.AvailablePhysicalBytes,
                Commit = mem.TotalCommittedBytes,
                CommitLimit = mem.TotalCommitLimitBytes,
                Procs = procs.Count,
                Threads = procs.Sum(p => p.Memory.ThreadCount),
                Mode = mode.ToString(),
                Interval = intervalSeconds,
                Top = procs.Where(p => p.Memory.PrivateWorkingSetBytes > 64L * 1024 * 1024)
                           .OrderByDescending(p => p.Memory.PrivateWorkingSetBytes)
                           .Take(10)
                           .Select(p => new UsageTopProc
                           {
                               N = p.Name,
                               W = p.Memory.PrivateWorkingSetBytes,
                               C = Math.Round(p.Memory.CpuPercent, 1),
                               T = p.Memory.ThreadCount
                           }).ToList()
            };
            File.AppendAllText(Path.Combine(_dir, $"samples-{now:yyyyMMdd}.jsonl"),
                JsonSerializer.Serialize(sample) + Environment.NewLine);
        }
        catch { }
    }

    public List<UsageSample> ReadWindow(TimeSpan window)
    {
        var result = new List<UsageSample>();
        try
        {
            var since = DateTime.UtcNow - window;
            var days = (int)window.TotalDays + 1;
            for (int i = 0; i <= days; i++)
            {
                var path = Path.Combine(_dir, $"samples-{DateTime.UtcNow.AddDays(-i):yyyyMMdd}.jsonl");
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    try
                    {
                        var s = JsonSerializer.Deserialize<UsageSample>(line);
                        if (s != null && s.T >= since) result.Add(s);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return result.OrderBy(s => s.T).ToList();
    }

    private void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var file in Directory.GetFiles(_dir, "samples-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // samples-20261028
                if (name.Length >= 15 && DateTime.TryParseExact(name[^8..], "yyyyMMdd", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var day) && day < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }
}
