using System.Diagnostics;
using System.Text.Json;
using MemPilot.Helpers;

namespace MemPilot.Services;

/// <summary>
/// 一次全量刷新的分段耗时（v0.20.0，毫秒）。
/// 用途：出现 8–12s 的慢 tick 时能直接看出卡在枚举、分类、显存采样还是 UI marshal。
/// </summary>
public sealed record RefreshTimings(long EnumMs, long ClassifyMs, long GpuMs, long UiMs)
{
    /// <summary>无数据（首帧/手动调用未填）。</summary>
    public static readonly RefreshTimings Zero = new(0, 0, 0, 0);
}

/// <summary>一分钟的自身开销聚合（写入 history/self-yyyyMMdd.jsonl）。</summary>
public class SelfCostSample
{
    /// <summary>窗口结束时刻（UTC）。</summary>
    public DateTime T { get; set; }
    /// <summary>本分钟全量刷新次数。</summary>
    public int N { get; set; }
    public long P50 { get; set; }
    public long P95 { get; set; }
    public long Max { get; set; }
    /// <summary>自身工作集（MB）。</summary>
    public long Rss { get; set; }
    /// <summary>自身 CPU 占用（%，按逻辑处理器归一）。</summary>
    public double Cpu { get; set; }
    /// <summary>本分钟新增的阶段失败数。</summary>
    public int Fails { get; set; }
    /// <summary>枚举进程的均值耗时（ms，v0.20.0）。</summary>
    public long EnumMs { get; set; }
    /// <summary>分类/模式保留/负载分类循环的均值耗时（ms，v0.20.0）。</summary>
    public long ClassifyMs { get; set; }
    /// <summary>显存采样的均值耗时（ms，v0.20.0）。慢 tick 常见元凶之一。</summary>
    public long GpuMs { get; set; }
    /// <summary>UI 线程 marshal 等待的均值耗时（ms，v0.20.0）。</summary>
    public long UiMs { get; set; }
}

/// <summary>
/// 自身开销记录（v0.18.0）。一个调度内存的常驻工具必须能自证它不是负担：
/// 旧实现只在状态栏拼了枚举/分类耗时，既不落盘也没有自身 RSS/CPU——用户无法验证
/// "MemPilot 自己占多少"，而这正是目标用户最在意的一件事。
///
/// 采集方式：全量刷新耗时由引擎用 Stopwatch 包住；轻探针每 5s 调一次 Heartbeat，
/// 承担跨分钟 flush、自身 RSS/CPU 采样与过期清理。每分钟一条，保留 30 天。
/// </summary>
public class SelfCostRecorder
{
    private readonly string _dir;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private readonly List<long> _ticks = new();
    private readonly List<RefreshTimings> _timings = new();
    private DateTime _windowStartUtc = DateTime.UtcNow;
    private DateTime _lastPruneUtc = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc = DateTime.UtcNow;
    private int _lastFailuresSeen;
    private int _failuresThisWindow;
    private SelfCostSample? _latest;
    private readonly Process _self = Process.GetCurrentProcess();

    public SelfCostRecorder(string dataDir, int retentionDays = 30)
    {
        _dir = Path.Combine(dataDir, "history");
        _retentionDays = retentionDays;
        try { Directory.CreateDirectory(_dir); } catch { }
        try { _lastCpuTime = _self.TotalProcessorTime; } catch { }
        DailyJsonl.Prune(_dir, "self", _retentionDays);
        _latest = Recent(1).FirstOrDefault();   // 预热：UI 读 Latest() 不再碰磁盘
    }

    public string DirectoryPath => _dir;

    /// <summary>每次全量刷新结束时调用（毫秒 + 分段耗时）。</summary>
    public void RecordTick(long elapsedMs, RefreshTimings timings, int stageFailuresTotal)
    {
        lock (_lock)
        {
            _ticks.Add(elapsedMs);
            _timings.Add(timings ?? RefreshTimings.Zero);
            TrackFailures(stageFailuresTotal);
        }
    }

    /// <summary>分段均值（internal 便于单测）：空列表 → 0。</summary>
    internal static long AverageOf(IReadOnlyList<RefreshTimings> list, Func<RefreshTimings, long> selector)
    {
        if (list.Count == 0) return 0;
        long sum = 0;
        foreach (var t in list) sum += selector(t);
        return (long)Math.Round((double)sum / list.Count);
    }

    /// <summary>轻探针每 5s 调用：跨分钟时落一条聚合，并顺带做过期清理。</summary>
    public void Heartbeat(int stageFailuresTotal)
    {
        SelfCostSample? sample = null;
        lock (_lock)
        {
            TrackFailures(stageFailuresTotal);
            var now = DateTime.UtcNow;
            if (now.Minute == _windowStartUtc.Minute && now.Hour == _windowStartUtc.Hour) return;

            sample = BuildLocked(now);
            _ticks.Clear();
            _timings.Clear();
            _failuresThisWindow = 0;
            _windowStartUtc = now;
        }
        Write(sample);
        if ((DateTime.UtcNow - _lastPruneUtc).TotalHours >= 24)
        {
            _lastPruneUtc = DateTime.UtcNow;
            DailyJsonl.Prune(_dir, "self", _retentionDays);
        }
    }

    private void TrackFailures(int total)
    {
        if (total > _lastFailuresSeen) _failuresThisWindow += total - _lastFailuresSeen;
        _lastFailuresSeen = total;
    }

    private SelfCostSample BuildLocked(DateTime nowUtc)
    {
        var ticks = _ticks.Count == 0 ? new List<long> { 0 } : _ticks.OrderBy(x => x).ToList();
        long Pct(double p) => ticks[Math.Min(ticks.Count - 1, (int)Math.Ceiling(p * ticks.Count) - 1)];

        long rss = 0;
        double cpu = 0;
        try
        {
            rss = _self.WorkingSet64 / 1048576;
            var cpuNow = _self.TotalProcessorTime;
            var wall = (nowUtc - _lastCpuSampleUtc).TotalSeconds;
            if (wall > 1)
                cpu = Math.Round((cpuNow - _lastCpuTime).TotalSeconds / wall / Environment.ProcessorCount * 100, 2);
            _lastCpuTime = cpuNow;
        }
        catch { }
        _lastCpuSampleUtc = nowUtc;

        return new SelfCostSample
        {
            T = nowUtc,
            N = _ticks.Count,
            P50 = Pct(0.50),
            P95 = Pct(0.95),
            Max = ticks[^1],
            Rss = rss,
            Cpu = cpu,
            Fails = _failuresThisWindow,
            EnumMs = AverageOf(_timings, t => t.EnumMs),
            ClassifyMs = AverageOf(_timings, t => t.ClassifyMs),
            GpuMs = AverageOf(_timings, t => t.GpuMs),
            UiMs = AverageOf(_timings, t => t.UiMs)
        };
    }

    /// <summary>
    /// 测试专用：强制结算当前窗口（生产由 Heartbeat 跨分钟触发）。
    /// 返回样本并清空累计，避免测试依赖真实时钟跨分钟。
    /// </summary>
    internal SelfCostSample? FlushForTests()
    {
        SelfCostSample? sample;
        lock (_lock)
        {
            sample = BuildLocked(DateTime.UtcNow);
            _ticks.Clear();
            _timings.Clear();
            _failuresThisWindow = 0;
            _windowStartUtc = DateTime.UtcNow;
        }
        Write(sample);
        return sample;
    }

    private void Write(SelfCostSample? sample)
    {
        if (sample == null) return;
        _latest = sample;
        try
        {
            DailyJsonl.Append(
                DailyJsonl.PathFor(_dir, "self", DateTime.Now),
                JsonSerializer.Serialize(sample));
        }
        catch (Exception ex) { Diag.Record("self-cost:write", ex); }
    }

    /// <summary>最近 n 天的聚合（新→旧），主页/诊断页展示用。</summary>
    public List<SelfCostSample> Recent(int days = 2)
        => DailyJsonl.ReadRecent<SelfCostSample>(_dir, "self", days)
                     .OrderByDescending(s => s.T)
                     .ToList();

    /// <summary>最新一条（内存预热，不读盘）；没有数据时返回 null。</summary>
    public SelfCostSample? Latest() => _latest;
}
