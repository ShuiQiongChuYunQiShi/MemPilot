using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 决策日志导出（v0.22.0）：CSV / JSON。
/// Reason 走 <see cref="DecisionText.Render"/>（当前语言）；未迁移的记录在 JSON 里
/// 标 `legacy=true`，便于使用者知道该行仍是中文原文。
/// </summary>
public static class DecisionExport
{
    public static string ToCsv(IEnumerable<DecisionEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time_utc,kind,target,decision,reason,code");
        foreach (var e in entries.OrderBy(x => x.TimestampUtc))
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(e.TimestampUtc.ToString("O")),
                Csv(e.Kind),
                Csv(e.Target),
                Csv(e.Decision),
                Csv(DecisionText.Render(e)),
                Csv(e.Code ?? "")
            }));
        }
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<DecisionEntry> entries)
    {
        var rows = entries.OrderBy(x => x.TimestampUtc).Select(e => new
        {
            timeUtc = e.TimestampUtc.ToString("O"),
            kind = e.Kind,
            target = e.Target,
            decision = e.Decision,
            reason = DecisionText.Render(e),
            code = e.Code,
            legacy = string.IsNullOrEmpty(e.Code)
        });
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>RFC 4180 转义：含逗号/引号/换行时加引号并双写引号。</summary>
    private static string Csv(string? s)
    {
        s ??= "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
