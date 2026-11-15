using MemPilot.Localization;

namespace MemPilot.Services;

/// <summary>
/// 界面术语单一来源（v0.11.0，UX 摸排 P1-1；v0.19.0 改为 i18n key）。
///
/// 复盘发现同一动作有 6 个名字（腾内存/腾挪/生成计划/压缩工作集/一键优化/L1 回收工作集）、
/// 同一概念 3 个名字（提交空闲/提交余量/CommitFree）。用户要学 6 个词才能确认它们是同一件事。
/// 本类是唯一的映射表；XAML 与代码里的用户可见文案一律从这里取词，
/// `UiTermsTests` 会扫描 XAML 把旧词挡回去。
///
/// 术语决策（用户确认，2026-11-04）：
/// - 页面概念统一「腾内存」（动词「腾」）
/// - L 级动作保留编号作副标签：回收工作集 (L1) / 挂起 (L2) / 优雅关闭 (L3) / 强制结束 (L4)
/// - 「提交余量」是 commit-free 的唯一名称
/// - 「保护」= 进程的客观属性（不可处置）；「保留」= 用户的主观标记（别清这个）
///
/// v0.19.0：所有词条改为 `UiStrings` 的 key（文案在
/// `src/MemPilot.App/Resources/Strings.{zh-CN,en}.xaml`），运行时切语言即时生效。
/// </summary>
public static class UiTerms
{
    // ---- 页面/流程概念 ----
    public static string MakeRoom => UiStrings.Get("Concept_MakeRoom");
    public static string MakeRoomVerb => UiStrings.Get("Concept_MakeRoom");

    // ---- 动作阶梯（L 级为副标签）----
    public static string L1Trim => UiStrings.Get("Level_L1Trim");
    public static string L2Suspend => UiStrings.Get("Level_L2Suspend");
    public static string L3Graceful => UiStrings.Get("Level_L3Graceful");
    public static string L4Kill => UiStrings.Get("Level_L4ForceKill");

    /// <summary>批量动作：「把所有候选的工作集回收一遍」——原来叫「一键优化」「压缩工作集」等 4 个名字。</summary>
    public static string TrimAllCandidates => UiStrings.Get("Level_TrimAllCandidates");

    // ---- 指标 ----
    public static string CommitFree => UiStrings.Get("Concept_CommitFree");

    // ---- 保护 / 保留 ----
    /// <summary>进程的客观属性：系统/安全/反作弊/驱动等，任何路径都不处置。</summary>
    public static string Protected => UiStrings.Get("Concept_Protected");
    /// <summary>用户的主观标记：以后别清这个进程。</summary>
    public static string KeepMark => UiStrings.Get("Concept_KeepMark");
    /// <summary>用户标记：这个进程可以清。</summary>
    public static string CleanableMark => UiStrings.Get("Concept_CleanableMark");

    // ---- 规则名单 ----
    public static string KeepPatterns => UiStrings.Get("Concept_KeepPatterns");
    public static string CleanPatterns => UiStrings.Get("Concept_CleanPatterns");

    /// <summary>给 CleanupActionLevel 的短名（含 L 级副标签）。供列/菜单显示用。</summary>
    public static string OfLevel(string level) => level switch
    {
        "L1Trim" => UiStrings.Get("Level_L1Trim"),
        "L2Suspend" => UiStrings.Get("Level_L2Suspend"),
        "L3GracefulClose" or "L3Request" => UiStrings.Get("Level_L3Graceful"),
        "L4ForceKill" => UiStrings.Get("Level_L4ForceKill"),
        "L0Purge" => UiStrings.Get("Level_L0Purge"),
        _ => level,
    };

    /// <summary>决策日志 Kind 的显示名（v0.11.0 P2-4：日志页不再亮内部枚举值）。
    /// v0.19.0：未知 Kind 原样返回（不显示 key）。</summary>
    public static string OfKind(string kind)
        => UiStrings.TryGet("Kind_" + kind, out var v) ? v : kind;

    /// <summary>审计 Action/Level 的显示名（v0.13.0：审计页不再亮内部枚举）。</summary>
    public static string OfAction(string action)
        => UiStrings.TryGet("Action_" + action, out var v) ? v : action;

    public static string OfLevel2(string level) => level switch
    {
        "L0Purge" => UiStrings.Get("Level_L0Purge"),
        "L0Purge+L1Trim" => UiStrings.Get("Level_L0PurgeL1"),
        "L1Trim" => UiStrings.Get("Level_L1Trim"),
        "L2Suspend" => UiStrings.Get("Level_L2Suspend"),
        "L3GracefulClose" => UiStrings.Get("Level_L3Graceful"),
        "L3Request" => UiStrings.Get("Level_L3Request"),
        "L4ForceKill" => UiStrings.Get("Level_L4ForceKill"),
        "Auto" => UiStrings.Get("Level_Auto"),
        _ => level,
    };

    /// <summary>分类依据：稳定英文 id → 当前语言文案；`program_catalog:` 前缀带参数。</summary>
    public static string OfCategoryReason(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return "";
        if (reason.StartsWith("program_catalog:", StringComparison.Ordinal))
        {
            var name = reason["program_catalog:".Length..];
            return UiStrings.F("Reason_program_catalog", name);
        }
        return UiStrings.TryGet("Reason_" + reason, out var v) ? v : reason;
    }

    /// <summary>进程分类：全名（复核队列）或短标签（密集网格列，v0.19.0 收敛两份重复映射）。</summary>
    public static string OfCategory(MemPilot.Models.ProcessCategory category, bool shortForm = false)
    {
        var key = (shortForm ? "CatShort_" : "Cat_") + category;
        return UiStrings.TryGet(key, out var v) ? v : category.ToString();
    }
}
