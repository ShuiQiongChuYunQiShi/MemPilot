namespace MemPilot.Services;

public enum PressureLevel { Roomy = 0, Normal = 1, Tight = 2, Critical = 3 }

public class HourPressure
{
    public int Hour { get; set; }
    public double MedianAvailGb { get; set; }
    public double P25AvailGb { get; set; }
    public int SampleCount { get; set; }
    public PressureLevel Level { get; set; }
}

/// <summary>
/// Aggregates usage history into a 24-hour available-memory profile (LOCAL time).
/// v1 uses only system-level fields on purpose: per-process Top history only starts
/// 2026-10-28 23:40 (pre-K32-fix samples recorded empty Top), so habit learning must
/// not depend on it yet. Advisory only — feeds hints, never automatic actions.
/// Levels are classified on P25 (conservative: a single quiet sample must not mask a
/// routinely tight hour).
/// </summary>
public class HabitProfile
{
    public List<HourPressure> Hours { get; } = new();
    public int SampleCount { get; set; }
    public DateTime BuiltAtUtc { get; set; }

    public static HabitProfile Build(IEnumerable<UsageSample> samples, DateTime? nowLocal = null)
    {
        const long Gb = 1024L * 1024 * 1024;
        var profile = new HabitProfile { BuiltAtUtc = DateTime.UtcNow };
        var byHour = new Dictionary<int, List<double>>();
        foreach (var s in samples)
        {
            if (s.T == DateTime.MinValue) continue;
            var local = s.T.ToLocalTime();
            if (nowLocal.HasValue && local > nowLocal.Value) continue;
            if (!byHour.TryGetValue(local.Hour, out var list)) byHour[local.Hour] = list = new List<double>();
            list.Add(s.Avail / (double)Gb);
            profile.SampleCount++;
        }

        for (int h = 0; h < 24; h++)
        {
            if (!byHour.TryGetValue(h, out var vals) || vals.Count == 0) continue;
            vals.Sort();
            var hp = new HourPressure
            {
                Hour = h,
                SampleCount = vals.Count,
                MedianAvailGb = Percentile(vals, 0.50),
                P25AvailGb = Percentile(vals, 0.25),
            };
            hp.Level = hp.P25AvailGb < 6 ? PressureLevel.Critical
                : hp.P25AvailGb < 10 ? PressureLevel.Tight
                : hp.P25AvailGb < 14 ? PressureLevel.Normal
                : PressureLevel.Roomy;
            profile.Hours.Add(hp);
        }
        return profile;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    public HourPressure? ForHour(int hour) => Hours.FirstOrDefault(x => x.Hour == hour);

    /// <summary>Compact Chinese summary of the tight windows for the history page.</summary>
    public string Describe()
    {
        if (Hours.Count == 0) return "";
        var tight = Hours.Where(h => h.Level is PressureLevel.Tight or PressureLevel.Critical)
                         .Select(h => h.Hour).OrderBy(x => x).ToList();
        if (tight.Count == 0) return "时段画像：各时段内存压力均衡，暂无常态紧张窗口";

        var ranges = new List<string>();
        int start = tight[0], prev = tight[0];
        foreach (var h in tight.Skip(1))
        {
            if (h == prev + 1) { prev = h; continue; }
            ranges.Add(RangeLabel(start, prev));
            start = prev = h;
        }
        ranges.Add(RangeLabel(start, prev));
        return $"时段画像：{string.Join("、", ranges)}常态偏紧（基于 {SampleCount} 条采样）";
    }

    private static string RangeLabel(int a, int b) => a == b ? $"{a:00} 点" : $"{a:00}-{(b + 1) % 24:00} 点";
}
