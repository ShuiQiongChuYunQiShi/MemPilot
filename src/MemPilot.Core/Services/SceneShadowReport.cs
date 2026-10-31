using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>影子模式一天的表现（本地日）。</summary>
public class ShadowDay
{
    public DateTime Date { get; set; }
    public int ShadowCount { get; set; }
    public int AutoSwitchCount { get; set; }
    public int ManualSwitchCount { get; set; }
    public int Agree { get; set; }
    public int Disagree { get; set; }
    public Dictionary<string, int> Targets { get; set; } = new(StringComparer.Ordinal);
    public string TargetSummary => Targets.Count == 0
        ? "—"
        : string.Join(" · ", Targets.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
}

/// <summary>
/// 影子模式结算（v0.3.2，棱镜 completeness/shadow-loop）。
///
/// 为什么需要它：影子模式此前「只写不读」——写入还按「目标变化 + 5 分钟」去重，
/// 无法从中算误判率，「跑一周再校准」实际执行不了。这里把 DecisionLog（影子/自动切换）
/// 与 AuditLog（人工切换）对齐成日聚合 + 可对照统计，输出可直接展示的结论。
///
/// 诚实的边界：只有用户手动切换过模式的那段时间才有"标签"可比，其余记为「未验证」。
/// 所以本报告同时给出**频率**（影子日均决断次数）——频率过高说明过敏感，与准确率无关也成立。
/// </summary>
public class SceneShadowReport
{
    /// <summary>影子与人工切换的对齐窗口（分钟）。</summary>
    public const int MatchWindowMinutes = 10;

    public int Days { get; private set; }
    public List<ShadowDay> Daily { get; } = new();

    public int ShadowTotal { get; private set; }
    public int AutoSwitchTotal { get; private set; }
    public int ManualSwitchTotal { get; private set; }
    public int Agree { get; private set; }
    public int Disagree { get; private set; }
    public int Unverified { get; private set; }

    /// <summary>可直接绑到界面的多行文本（第一行概况，第二行可对照统计，第三行建议）。</summary>
    public string Text { get; private set; } = "";

    public static SceneShadowReport Build(
        IEnumerable<DecisionEntry> decisions,
        IEnumerable<AuditEntry> audits,
        DateTime nowUtc,
        int days = 7)
    {
        var report = new SceneShadowReport { Days = Math.Max(1, days) };
        var todayLocal = nowUtc.ToLocalTime().Date;

        for (int i = report.Days - 1; i >= 0; i--)
            report.Daily.Add(new ShadowDay { Date = todayLocal.AddDays(-i) });

        ShadowDay? Bucket(DateTime utc)
        {
            var d = utc.ToLocalTime().Date;
            return report.Daily.FirstOrDefault(x => x.Date == d);
        }

        var shadows = new List<(DateTime at, string target, ShadowDay day)>();
        foreach (var e in decisions)
        {
            if (!string.Equals(e.Kind, "SceneShadow", StringComparison.Ordinal)) continue;
            var day = Bucket(e.TimestampUtc);
            if (day == null) continue;
            day.ShadowCount++;
            day.Targets[e.Target] = day.Targets.TryGetValue(e.Target, out var n) ? n + 1 : 1;
            shadows.Add((e.TimestampUtc, e.Target, day));
            report.ShadowTotal++;
        }

        foreach (var e in decisions)
        {
            if (!string.Equals(e.Kind, "ModeSwitch", StringComparison.Ordinal)) continue;
            var day = Bucket(e.TimestampUtc);
            if (day == null) continue;
            day.AutoSwitchCount++;
            report.AutoSwitchTotal++;
        }

        var manual = new List<(DateTime at, string target, ShadowDay day)>();
        foreach (var a in audits)
        {
            if (!string.Equals(a.Action, "ModeSwitch", StringComparison.Ordinal)) continue;
            var day = Bucket(a.TimestampUtc);
            if (day == null) continue;
            day.ManualSwitchCount++;
            report.ManualSwitchTotal++;
            manual.Add((a.TimestampUtc, ParseManualTarget(a.Target), day));
        }

        // 对齐：影子决断前后 MatchWindowMinutes 内若有人工切换，则同向=一致，异向=分歧，否则未验证。
        foreach (var s in shadows)
        {
            var near = manual.Where(m => Math.Abs((m.at - s.at).TotalMinutes) <= MatchWindowMinutes).ToList();
            if (near.Count == 0) { report.Unverified++; continue; }
            if (near.Any(m => string.Equals(m.target, s.target, StringComparison.OrdinalIgnoreCase)))
            { report.Agree++; s.day.Agree++; }
            else
            { report.Disagree++; s.day.Disagree++; }
        }

        report.Text = Compose(report);
        return report;
    }

    /// <summary>人工切换的 Target 形如 "Resident → Game"（MainViewModel.CurrentMode setter）。</summary>
    private static string ParseManualTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";
        var idx = target.LastIndexOf('→');
        return idx >= 0 ? target[(idx + 1)..].Trim() : target.Trim();
    }

    private static string Compose(SceneShadowReport r)
    {
        var perDay = r.ShadowTotal / (double)r.Days;
        var head = $"近 {r.Days} 天：影子决断 {r.ShadowTotal} 次（日均 {perDay:F1}），自动切换 {r.AutoSwitchTotal} 次，人工切换 {r.ManualSwitchTotal} 次";

        var comparable = r.Agree + r.Disagree;
        var mid = comparable == 0
            ? $"可对照 0 次（影子 {r.Unverified} 次均无人工切换作标签）"
            : $"可对照 {comparable} 次：一致 {r.Agree} / 分歧 {r.Disagree}（{r.Agree * 100.0 / comparable:F0}%），未验证 {r.Unverified} 次";

        string advice;
        if (r.ShadowTotal == 0)
        {
            advice = "建议：影子没有任何决断——阈值（60 / 领先 20）可能偏高，或这些天场景信号不足；先看决策日志里的打分再调。";
        }
        else if (comparable == 0)
        {
            advice = $"建议：没有人工切换可对照，算不出准确率，只能看频率——日均 {perDay:F1} 次" +
                     (perDay > 12 ? "偏高（自动切换会频繁跳变，先抬阈值或加迟滞）。" : "属正常区间，继续积累标签。");
        }
        else
        {
            var rate = r.Agree * 100.0 / comparable;
            advice = rate >= 80
                ? $"建议：影子与人工判断一致率 {rate:F0}%，可考虑开自动切换；先只开 AI 场景小步验证。"
                : rate >= 50
                    ? $"建议：一致率 {rate:F0}%，先补信号权重（前台标题 / GPU / 输入活跃）再开自动。"
                    : $"建议：一致率 {rate:F0}%，不要开自动切换；先复核阈值与信号打分。";
            if (perDay > 12) advice += $" 另：日均 {perDay:F1} 次偏敏感，需加迟滞。";
        }

        return head + "\n" + mid + "\n" + advice;
    }
}
