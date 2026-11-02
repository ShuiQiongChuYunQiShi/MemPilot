using System.IO;
using System.Text.Json;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>一条 GPU 样本里的单个进程（短字段名控制文件体积，与 UsageTopProc 一致）。</summary>
public class GpuSampleProc
{
    public string N { get; set; } = "";   // 进程名
    public int I { get; set; }            // pid（回溯时用来对上具体实例；重启后 pid 会复用，仅作线索）
    public long D { get; set; }           // 专用显存（Local Usage）
    public long S { get; set; }           // 共享显存（Non Local Usage）
    public double? P { get; set; }        // GPU%（null = 该进程不在 GPU Engine 列表，不写）
}

/// <summary>一条显存采样（默认 30 秒一条，与内存采样分文件）。</summary>
public class GpuSample
{
    public DateTime T { get; set; }
    public int GapSec { get; set; }
    public string Event { get; set; } = "";  // "" / "resume" / "gpu-spike"
    public long? Used { get; set; }          // 已用（null = 当时不可用，绝不用 0 冒充）
    public long Total { get; set; }          // 适配器总显存（0 = 未知）
    public string Src { get; set; } = "none";// dxgi / pdh-adapter / pdh-process / none
    public int Procs { get; set; }           // 有显存归属的进程数
    public List<GpuSampleProc> Top { get; set; } = new();

    public double? UsedPercent => Used.HasValue && Total > 0 ? (double)Used.Value / Total * 100 : null;
}

/// <summary>
/// 显存采样落盘（v0.3.4）：`%APPDATA%\MemPilot\history\gpu-yyyyMMdd.jsonl`，按本地日切分、
/// 沿用 UsageRetentionDays 保留。与内存采样分开的理由：显存要 30 秒一条（AI 生成尖峰只有 1–2 分钟），
/// 混进 5 分钟的内存样本会把文件撑大且语义混乱。
/// </summary>
public sealed class GpuRecorder
{
    private readonly string _dir;
    private readonly int _retentionDays;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private long? _lastUsedBytes;

    /// <summary>显存跳增判定阈值：单条样本内增长 ≥4 GB 记 event=gpu-spike（只标记，不告警）。</summary>
    public const long SpikeThresholdBytes = 4L * 1024 * 1024 * 1024;

    public GpuRecorder(string dataDir, int retentionDays = 30)
    {
        _dir = Path.Combine(dataDir, "history");
        _retentionDays = Math.Max(1, retentionDays);
        try { Directory.CreateDirectory(_dir); } catch { }
        _lastSampleUtc = LoadLastSampleUtc();
        Prune();
    }

    private string PathFor(DateTime localDate) => Path.Combine(_dir, $"gpu-{localDate:yyyyMMdd}.jsonl");

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
                var s = JsonSerializer.Deserialize<GpuSample>(line);
                if (s != null) { _lastUsedBytes = s.Used; return s.T.ToUniversalTime(); }
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    /// <summary>写一条样本并返回它（Monitor 用它做 Last/诊断）。</summary>
    public GpuSample Write(GpuMemorySnapshot mem, IReadOnlyList<ProcessInfo> procs,
        IReadOnlyList<GpuAdapterInfo> adapters, IReadOnlyDictionary<int, double>? gpuPercent,
        int intervalSeconds, DateTime nowUtc, Func<int, string?>? nameResolver = null)
    {
        int gapSec = _lastSampleUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (int)Math.Round((nowUtc - _lastSampleUtc).TotalSeconds));

        var byPid = new Dictionary<int, ProcessInfo>(procs.Count);
        foreach (var p in procs) byPid[p.Pid] = p;

        var sample = new GpuSample
        {
            T = nowUtc,
            GapSec = gapSec,
            Used = mem.UsedBytes,
            Total = adapters.Sum(a => a.DedicatedVideoMemoryBytes),
            Src = mem.Source,
            Procs = mem.ProcessDedicated.Count,
            Top = mem.ProcessDedicated
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => new
                {
                    Pid = kv.Key,
                    // v0.5.0：进程快照里没有的 pid 用 monitor 的缓存名兜底；仍未知就跳过，
                    // 不再写 "pid 12345" 这种匿名条目（真机 28 条，回溯时毫无用处）
                    Name = byPid.TryGetValue(kv.Key, out var p) ? p.Name : nameResolver?.Invoke(kv.Key),
                    Dedicated = kv.Value,
                    Shared = mem.ProcessShared.GetValueOrDefault(kv.Key),
                    Gpu = gpuPercent != null && gpuPercent.TryGetValue(kv.Key, out var g) && g >= 0
                        ? (double?)Math.Round(g, 1) : null
                })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .Select(x => new GpuSampleProc
                {
                    N = x.Name!,
                    I = x.Pid,
                    D = x.Dedicated,
                    S = x.Shared,
                    P = x.Gpu
                }).ToList()
        };

        // 事件标记：醒来/重启 vs 显存跳增（供事后复盘定位"AI 开始/结束"时刻）
        if (intervalSeconds > 0 && gapSec > intervalSeconds * 3) sample.Event = "resume";
        else if (sample.Used.HasValue && _lastUsedBytes.HasValue &&
                 sample.Used.Value - _lastUsedBytes.Value >= SpikeThresholdBytes) sample.Event = "gpu-spike";

        try
        {
            File.AppendAllText(PathFor(nowUtc.ToLocalTime()),
                JsonSerializer.Serialize(sample) + Environment.NewLine);
            _lastSampleUtc = nowUtc;
            _lastUsedBytes = sample.Used;
        }
        catch { }

        return sample;
    }

    public List<GpuSample> ReadWindow(TimeSpan window)
    {
        var result = new List<GpuSample>();
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
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var s = JsonSerializer.Deserialize<GpuSample>(line);
                        if (s != null && s.T >= since) result.Add(s);
                    }
                    catch { } // 损坏行跳过：一条坏样本不该让整页历史消失
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
            foreach (var file in Directory.GetFiles(_dir, "gpu-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // gpu-20261101
                if (name.Length >= 12 &&
                    DateTime.TryParseExact(name[^8..], "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var day) &&
                    day.Date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch { }
    }
}
