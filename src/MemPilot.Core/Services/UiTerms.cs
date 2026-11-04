namespace MemPilot.Services;

/// <summary>
/// 界面术语单一来源（v0.11.0，UX 摸排 P1-1）。
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
/// </summary>
public static class UiTerms
{
    // ---- 页面/流程概念 ----
    public const string MakeRoom = "腾内存";
    public const string MakeRoomVerb = "腾";

    // ---- 动作阶梯（中文为主，L 级为副标签）----
    public const string L1Trim = "回收工作集 (L1)";
    public const string L1TrimShort = "回收工作集";
    public const string L2Suspend = "挂起 (L2)";
    public const string L2SuspendShort = "挂起";
    public const string L3Graceful = "优雅关闭 (L3)";
    public const string L3GracefulShort = "优雅关闭";
    public const string L4Kill = "强制结束 (L4)";
    public const string L4KillShort = "强制结束";

    /// <summary>批量动作：「把所有候选的工作集回收一遍」——原来叫「一键优化」「压缩工作集」等 4 个名字。</summary>
    public const string TrimAllCandidates = "回收闲置工作集";

    // ---- 指标 ----
    public const string CommitFree = "提交余量";

    // ---- 保护 / 保留 ----
    /// <summary>进程的客观属性：系统/安全/反作弊/驱动等，任何路径都不处置。</summary>
    public const string Protected = "保护";
    /// <summary>用户的主观标记：以后别清这个进程。</summary>
    public const string KeepMark = "保留";
    /// <summary>用户标记：这个进程可以清。</summary>
    public const string CleanableMark = "可清理";

    // ---- 规则名单 ----
    public const string KeepPatterns = "保留名单";
    public const string CleanPatterns = "清理名单";

    /// <summary>给 CleanupActionLevel 的中文短名（含 L 级副标签）。供列/菜单显示用。</summary>
    public static string OfLevel(string level) => level switch
    {
        "L1Trim" => L1Trim,
        "L2Suspend" => L2Suspend,
        "L3GracefulClose" or "L3Request" => L3Graceful,
        "L4ForceKill" => L4Kill,
        "L0Purge" => "清理备用列表 (L0)",
        _ => level,
    };

    /// <summary>决策日志 Kind 的中文名（v0.11.0 P2-4：日志页不再亮内部枚举值）。</summary>
    public static string OfKind(string kind) => kind switch
    {
        "Lease" => "租约",
        "AiSession" => "AI 会话",
        "CommitRelief" => "提交兜底",
        "CommitWatch" => "提交观察",
        "ModeSwitch" => "模式切换",
        "ModeSwitchSuppressed" => "模式切换抑制",
        "ModeSwitchStorm" => "模式切换风暴",
        "RuleAudit" => "规则体检",
        "PromoteRule" => "规则晋升建议",
        "Diag" => "诊断",
        "GrowthWatch" => "内存增长观察",
        "SceneShadow" => "场景影子",
        "HabitHint" => "时段提醒",
        "AutoCleanup" => "自动清理",
        "AutoJunkSweep" => "常驻垃圾清扫",
        "AutoOptimize" => "系统内存自动优化（已废弃）",
        _ => kind,
    };
}
