using System.IO;
using System.Text.Json;

namespace MemPilot.Helpers;

/// <summary>
/// 按天滚动的 JSONL 存储（v0.18.0 从 <c>UsageRecorder</c> 提取）。
/// 历史采样与自身开销记录共用"每天一个文件 + 定期清理"，避免两处各写一份格式约定。
/// </summary>
public static class DailyJsonl
{
    /// <summary>{dir}/{prefix}-yyyyMMdd.jsonl</summary>
    public static string PathFor(string dir, string prefix, DateTime localDate)
        => Path.Combine(dir, $"{prefix}-{localDate:yyyyMMdd}.jsonl");

    public static void Append(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    public static void Append<T>(string path, T value, JsonSerializerOptions? options = null)
        => Append(path, JsonSerializer.Serialize(value, options));

    /// <summary>删除早于 today-retentionDays 的 {prefix}-*.jsonl，返回删除数。</summary>
    public static int Prune(string dir, string prefix, int retentionDays)
    {
        int removed = 0;
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
            foreach (var file in Directory.GetFiles(dir, $"{prefix}-*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);   // prefix-20261028
                if (name.Length >= 9 &&
                    DateTime.TryParseExact(name[^8..], "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var day) &&
                    day.Date < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>读取最近 days 天内所有 {prefix}-*.jsonl 的行（包含解析失败跳过）。</summary>
    public static IEnumerable<T> ReadRecent<T>(string dir, string prefix, int days)
    {
        var result = new List<T>();
        try
        {
            for (int i = 0; i <= days; i++)
            {
                var path = PathFor(dir, prefix, DateTime.Now.AddDays(-i));
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var item = JsonSerializer.Deserialize<T>(line);
                        if (item != null) result.Add(item);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return result;
    }
}
