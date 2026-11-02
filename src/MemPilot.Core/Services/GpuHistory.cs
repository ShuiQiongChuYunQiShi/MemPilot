namespace MemPilot.Services;

/// <summary>显存大户聚合行（历史页表格）。</summary>
public sealed class GpuHistoryRow
{
    public string Name { get; set; } = "";
    public long PeakDedicatedBytes { get; set; }
    public long AvgDedicatedBytes { get; set; }
    public long PeakSharedBytes { get; set; }
    public int Seen { get; set; }
}

/// <summary>曲线上的一个点（T 为 UTC，Percent 为 0–100）。</summary>
public readonly struct GpuHistoryPoint
{
    public GpuHistoryPoint(DateTime t, double percent) { T = t; Percent = percent; }
    public DateTime T { get; }
    public double Percent { get; }
}

/// <summary>
/// 显存历史聚合（v0.3.4）：纯函数，供历史页与将来的 AI 决策复用。
///
/// 两条原则：
/// ① 缺数据不等于 0 —— 只有 Used 与 Total 都有效才进曲线（否则跳过，不画假谷底）；
/// ② 与内存历史同样按"出现采样数"统计，但显存按进程名的峰值/均值排序（AI 生成进程一目了然）。
/// </summary>
public static class GpuHistory
{
    public static GpuHistoryResult Build(IEnumerable<GpuSample> samples)
    {
        var result = new GpuHistoryResult();
        var byName = new Dictionary<string, (long peakD, long sumD, long peakS, int n)>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in samples)
        {
            if (s == null) continue;
            result.SampleCount++;
            if (s.Event == "gpu-spike") result.SpikeCount++;
            if (s.Event == "resume") result.ResumeCount++;

            if (s.Used.HasValue)
            {
                result.LastUsed = s.Used;
                result.PeakUsed = result.PeakUsed.HasValue ? Math.Max(result.PeakUsed.Value, s.Used.Value) : s.Used;
            }
            if (s.Total > 0) result.Total = s.Total;
            if (!string.IsNullOrEmpty(s.Src) && s.Src != "none") result.Source = s.Src;

            if (s.UsedPercent is { } pct) result.Points.Add(new GpuHistoryPoint(s.T, pct));

            foreach (var tp in s.Top)
            {
                if (string.IsNullOrEmpty(tp.N)) continue;
                var cur = byName.TryGetValue(tp.N, out var v)
                    ? v
                    : (peakD: 0L, sumD: 0L, peakS: 0L, n: 0);
                byName[tp.N] = (Math.Max(cur.peakD, tp.D), cur.sumD + tp.D, Math.Max(cur.peakS, tp.S), cur.n + 1);
            }
        }

        // 会话段：与实时检测共用 AiSessionDetector，保证"历史看到的"和"当时记的"一致
        var detector = new AiSessionDetector();
        foreach (var s in samples.OrderBy(x => x.T))
        {
            var update = detector.Push(s.Used, s.Total, s.T, s.Top);
            if (update.Event == AiSessionEvent.Ended && update.Summary != null)
            {
                result.Sessions.Add(update.Summary);
                if (result.Sessions.Count > 20) result.Sessions.RemoveAt(0);
            }
        }
        if (detector.Active && detector.Current is { } ongoing) result.Sessions.Add(ongoing);

        result.Top = byName
            .OrderByDescending(kv => kv.Value.Item1)
            .Take(10)
            .Select(kv => new GpuHistoryRow
            {
                Name = kv.Key,
                PeakDedicatedBytes = kv.Value.Item1,
                AvgDedicatedBytes = kv.Value.n == 0 ? 0 : kv.Value.Item2 / kv.Value.n,
                PeakSharedBytes = kv.Value.Item3,
                Seen = kv.Value.n
            })
            .ToList();

        return result;
    }
}

public sealed class GpuHistoryResult
{
    public int SampleCount { get; set; }
    public int SpikeCount { get; set; }
    public int ResumeCount { get; set; }
    public long? PeakUsed { get; set; }
    public long? LastUsed { get; set; }
    public long Total { get; set; }
    public string Source { get; set; } = "";
    public List<GpuHistoryRow> Top { get; set; } = new();
    public List<GpuHistoryPoint> Points { get; } = new();
    /// <summary>AI 会话段（v0.5.0，用同一个检测器离线回放；最多保留最近 20 段）。</summary>
    public List<AiSessionSummary> Sessions { get; } = new();
}
