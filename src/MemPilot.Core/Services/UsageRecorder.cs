using System.IO;
using System.Text.Json;
using MemPilot.Models;

namespace MemPilot.Services;

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
    public int GapMinutes { get; set; }     // minutes since the previous sample (0 = first ever)
    public string Event { get; set; } = ""; // "resume" when the gap means sleep/restart, not observation
    public List<UsageTopProc> Top { get; set; } = new();
}

/// <summary>
/// Persists usage habits as JSONL (one line per sample, one file per LOCAL day, 5-minute
/// cadence — ~288 lines/day, tiny). Local-date naming matters: UTC naming split 00:00–08:00
/// local samples into the previous day's file and made per-day habit stats lie.
/// Retention prunes files older than the configured number of days.
/// </summary>
public class UsageRecorder
{
    private readonly string _dir;
    private readonly int _retentionDays;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;

    public UsageRecorder(string dataDir, int retentionDays = 30)
    {
        _dir = Path.Combine(dataDir, "history");
        _retentionDays = retentionDays;
        try { Directory.CreateDirectory(_dir); } catch { }
        _lastSampleUtc = LoadLastSampleUtc();
        Prune();
    }

    private string PathFor(DateTime localDate) => Path.Combine(_dir, $"samples-{localDate:yyyyMMdd}.jsonl");

    /// <summary>Seed the gap baseline from the newest persisted sample so restarts and
    /// sleep/wake windows are distinguishable in the history.</summary>
    private DateTime LoadLastSampleUtc()
    {
        try
        {
            for (int i = 0; i <= 1; i++)
            {
                var path = PathFor(DateTime.Now.AddDays(-i));
                if (!File.Exists(path)) continue;
                var line = File.ReadLines(path).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (line == null) continue;
                var s = JsonSerializer.Deserialize<UsageSample>(line);
                if (s != null) return s.T.ToUniversalTime();
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    /// <summary>Called every engine tick; writes at most one sample per 5 minutes.</summary>
    public void RecordSample(SystemMemoryInfo mem, List<ProcessInfo> procs, AppMode mode, int intervalSeconds)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastWriteUtc).TotalMinutes < 5) return;
        _lastWriteUtc = now;

        try
        {
            Directory.CreateDirectory(_dir);
            int gapMinutes = _lastSampleUtc == DateTime.MinValue
                ? 0
                : Math.Max(0, (int)Math.Round((now - _lastSampleUtc).TotalMinutes));
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
                GapMinutes = gapMinutes,
                Event = gapMinutes >= 10 ? "resume" : "",
                // 32MB 门槛（原 64MB）：实测残留服务多为 50–110MB（QQLiveService 110MB、
                // vivo/ASUS 小组件 50–80MB），64MB 门槛把它们挡在历史外，习惯画像看不见。
                Top = procs.Where(p => p.Memory.PrivateWorkingSetBytes > 32L * 1024 * 1024)
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
            File.AppendAllText(PathFor(DateTime.Now),
                JsonSerializer.Serialize(sample) + Environment.NewLine);
            _lastSampleUtc = now;
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
                var path = PathFor(DateTime.Now.AddDays(-i));
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
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.GetFiles(_dir, "samples-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // samples-20261028
                if (name.Length >= 15 && DateTime.TryParseExact(name[^8..], "yyyyMMdd", null, System.Globalization.DateTimeStyles.AssumeLocal, out var day) && day.Date < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }
}
