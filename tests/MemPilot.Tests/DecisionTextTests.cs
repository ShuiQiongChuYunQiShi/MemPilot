using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.22.0：决策日志 Reason 的稳定 code + 双语模板，以及 CSV/JSON 导出。
/// </summary>
public class DecisionTextTests
{
    private static DecisionEntry E(string reason = "中文原文", string? code = null, params string[] args)
        => new()
        {
            TimestampUtc = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc),
            Kind = "AutoCleanup",
            Target = "demo (PID 1)",
            Decision = "跳过",
            Reason = reason,
            Code = code,
            Args = args.Length == 0 ? null : args
        };

    private static Func<string, string?> Dict(params (string k, string v)[] items)
    {
        var d = items.ToDictionary(x => x.k, x => x.v);
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    [Fact]
    public void KeyOf_UsesUnderscoreForm()
        => Assert.Equal("Decision_junk_advice_suggested", DecisionText.KeyOf("junk_advice.suggested"));

    [Fact]
    public void Render_UsesResolverInBothLanguages()
    {
        var entry = E(code: "junk_advice.suggested", args: new[] { "wps", "120" });

        var zh = Dict(("Decision_junk_advice_suggested", "建议处理 {0}（预计释放 {1} MB）"));
        var en = Dict(("Decision_junk_advice_suggested", "Suggested handling {0} ({1} MB)"));

        Assert.Equal("建议处理 wps（预计释放 120 MB）", DecisionText.Render(entry, zh));
        Assert.Equal("Suggested handling wps (120 MB)", DecisionText.Render(entry, en));
    }

    [Fact]
    public void Render_FallsBackToOriginalReason_WhenNoCode()
        => Assert.Equal("中文原文", DecisionText.Render(E(), Dict(("Decision_x", "x"))));

    [Fact]
    public void Render_FallsBackToOriginalReason_WhenTemplateMissing()
        => Assert.Equal("中文原文", DecisionText.Render(E(code: "not.migrated"), Dict(("Decision_other", "x"))));

    [Fact]
    public void Render_HandlesNoArgsAndBrokenTemplates()
    {
        Assert.Equal("无参数模板",
            DecisionText.Render(E(code: "no.args"), Dict(("Decision_no_args", "无参数模板"))));

        // 模板要求 {1} 但只给 1 个参数 → 回退模板原文（不抛）
        Assert.Equal("需要两个 {0} {1}",
            DecisionText.Render(E(code: "broken", args: new[] { "only" }), Dict(("Decision_broken", "需要两个 {0} {1}"))));
    }

    [Fact]
    public void Csv_HasHeaderAndRfc4180Escaping()
    {
        var entries = new[]
        {
            E(reason: "含,逗号"),
            E(reason: "含\"引号\""),
            E(reason: "含\n换行"),
        };

        var csv = DecisionExport.ToCsv(entries);
        var lines = csv.Split('\n');

        Assert.StartsWith("time_utc,kind,target,decision,reason,code", lines[0]);
        Assert.Contains("\"含,逗号\"", csv);
        Assert.Contains("\"含\"\"引号\"\"\"", csv);
        Assert.Contains("\"含\n换行\"", csv);
    }

    [Fact]
    public void Csv_OrdersByTimeAscending()
    {
        var older = E(reason: "older");
        older.TimestampUtc = new DateTime(2026, 11, 22, 1, 0, 0, DateTimeKind.Utc);
        var newer = E(reason: "newer");
        newer.TimestampUtc = new DateTime(2026, 11, 23, 1, 0, 0, DateTimeKind.Utc);

        var csv = DecisionExport.ToCsv(new[] { newer, older });
        Assert.True(csv.IndexOf("older", StringComparison.Ordinal) < csv.IndexOf("newer", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_MarksLegacyRows()
    {
        var migrated = E(code: "junk_advice.suggested", args: new[] { "a", "1" });
        var legacy = E();

        var json = DecisionExport.ToJson(new[] { migrated, legacy });

        Assert.Contains("\"code\": \"junk_advice.suggested\"", json);
        Assert.Contains("\"legacy\": true", json);
        Assert.Contains("\"legacy\": false", json);
        // 中文不转义（UnsafeRelaxedJsonEscaping）
        Assert.Contains("中文原文", json);
    }
}
