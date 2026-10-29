namespace MemPilot.Services;

/// <summary>
/// 历史使用分析：把 UsageRecorder 的 Top 采样聚合成"腾挪预测"用的进程权重。
/// 权重 = 出镜率(0..1) × 峰值 P90(GB)——反复出现且体量大的进程在候选排序中靠前。
/// 只影响 MakeRoomPlan 的排序，从不改变候选资格或风险等级（Unknown/受保护等硬排除不受影响）。
/// </summary>
public static class UsageAnalytics
{
    private const long Gb = 1024L * 1024 * 1024;

    public static Dictionary<string, double> ComputeProcessWeights(IEnumerable<UsageSample> samples)
    {
        var byName = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        int sampleCount = 0;
        foreach (var s in samples)
        {
            sampleCount++;
            foreach (var tp in s.Top)
            {
                if (!byName.TryGetValue(tp.N, out var list)) byName[tp.N] = list = new List<long>();
                list.Add(tp.W);
            }
        }

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (sampleCount == 0) return result;

        foreach (var kv in byName)
        {
            var vals = kv.Value;
            vals.Sort();
            double p90Gb = Percentile(vals, 0.90) / (double)Gb;
            double rate = Math.Min(1.0, (double)vals.Count / sampleCount);
            result[kv.Key] = rate * p90Gb;
        }
        return result;
    }

    private static double Percentile(List<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}
