using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 用途分类（v0.3.1）：在进程"身份"（ProcessCategory）之上派生"用途"（娱乐/工作/AI/系统/其他）。
/// 判定顺序：强类别（游戏/AI/系统）→ 名称与清单关键词 → 发布者关键词 → 其余 Unknown/Other。
/// 原则：歧义场景（浏览器、通用三方、聊天工具）不硬猜；用途只用于展示与排序，不构成处置依据。
/// </summary>
public static class WorkloadClassifier
{
    /// <summary>默认名单走内置（v0.4.0：可被 workload-rules.json 覆盖；静态调用保持旧行为）。</summary>
    public static void Apply(ProcessInfo p) => Apply(p, null, null);

    /// <summary>
    /// 完整判定。顺序：① 用户标记（右键「标记用途」）→ ② 强类别 → ③ 名单/发布者 → ④ 其他/未知。
    /// userLookup 返回 null 表示未标记；返回任何值都直接采用（包括 Other），便于"纠正成其他"。
    /// </summary>
    public static void Apply(ProcessInfo p, WorkloadRules? rules, Func<ProcessInfo, WorkloadKind?>? userLookup)
    {
        // 0) 用户标记优先：人工判断永远压过规则（错判不必再等发版）
        var user = userLookup?.Invoke(p);
        if (user.HasValue)
        {
            Set(p, user.Value, "user_mark");
            return;
        }

        rules ??= WorkloadRules.Defaults();

        switch (p.Category)
        {
            case ProcessCategory.Game:
            case ProcessCategory.GameLauncher:
            case ProcessCategory.AntiCheat:
                Set(p, WorkloadKind.Entertainment, "category:game"); return;
            case ProcessCategory.AiImageVideo:
                Set(p, WorkloadKind.Ai, "category:ai"); return;
            case ProcessCategory.SystemCritical:
            case ProcessCategory.SystemService:
            case ProcessCategory.Driver:
            case ProcessCategory.SecuritySoftware:
                Set(p, WorkloadKind.System, "category:system"); return;
            case ProcessCategory.DevTool:
            case ProcessCategory.ContainerVm:
                Set(p, WorkloadKind.Work, "category:dev"); return;
        }

        // 垃圾常驻不参与用途判定（WPS 推广组件不能被当成"工作"）
        if (p.Category == ProcessCategory.Bloatware)
        { Set(p, WorkloadKind.Other, "junk"); return; }

        // 2) 名称 / 清单显示名关键词（短名精确匹配，长名子串）
        var name = p.Name;
        var programName = p.ProgramDisplayName;
        if (MatchesAny(name, rules.EntertainmentNames) || MatchesAny(programName, rules.EntertainmentNames))
        { Set(p, WorkloadKind.Entertainment, "name:entertainment"); return; }
        if (rules.WorkExactNames.Contains(name) || rules.WorkExactNames.Contains(programName))
        { Set(p, WorkloadKind.Work, "name:work_exact"); return; }
        if (MatchesAny(name, rules.WorkNames) || MatchesAny(programName, rules.WorkNames))
        { Set(p, WorkloadKind.Work, "name:work"); return; }

        // 3) 清单发布者关键词（签名不可用时的补充证据）
        var publisher = p.ProgramPublisher;
        if (MatchesAny(publisher, rules.EntertainmentPublishers))
        { Set(p, WorkloadKind.Entertainment, "publisher:entertainment"); return; }
        if (MatchesAny(publisher, rules.WorkPublishers))
        { Set(p, WorkloadKind.Work, "publisher:work"); return; }

        // 4) 其余：明确是"装了的三方程序"记 Other（便于和未识别区分），否则 Unknown
        if (!string.IsNullOrEmpty(programName) || p.Category == ProcessCategory.ThirdPartyApp)
            Set(p, WorkloadKind.Other, "other");
        else
            Set(p, WorkloadKind.Unknown, "unknown");
    }

    private static void Set(ProcessInfo p, WorkloadKind kind, string reason)
    {
        p.Workload = kind;
        p.WorkloadReason = reason;
    }

    private static bool MatchesAny(string? text, List<string> words)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var w in words)
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string DisplayName(WorkloadKind kind) => WorkloadText.Of(kind);
}
