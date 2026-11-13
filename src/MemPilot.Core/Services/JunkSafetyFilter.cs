using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 垃圾候选的**唯一**安全过滤实现（v0.17.0）。
///
/// 为什么抽出来：JunkSweeper.BuildPlan 与 JunkAdvisor 的「用户标记可清理」通道
/// 用的是同一套七条排除规则，v0.15.1 靠"两份代码 + 注释声明必须同步"维系——
/// 这正是 v0.9.2 复盘里"安全判定 6 份收敛到 1 份"要消灭的模式。
/// 现在两边都走这里，改一处即生效。
///
/// 规则（顺序即优先级）：
/// ① 受保护 / 系统进程 / 服务
/// ② 危险类别（系统关键/安全软件/反作弊/驱动/未识别）
/// ③ 前台 PID / 正在发声
/// ④ 用户白名单标记（IsWhitelisted）
/// ⑤ 白名单名字/路径子串
/// ⑥ 最小存活期
/// </summary>
public static class JunkSafetyFilter
{
    public static bool IsEligible(
        ProcessInfo p,
        int foregroundPid,
        IReadOnlySet<int> audioPids,
        IReadOnlyCollection<string> whitelist,
        int minAgeSeconds,
        DateTime nowLocal)
    {
        if (p.IsProtected || p.IsSystemProcess || p.IsService) return false;

        // v0.18.0：永不建议/处置 MemPilot 自己（托盘模式下它满足"无窗口、低 CPU"）
        if (p.Pid == Environment.ProcessId) return false;

        if (p.Category is ProcessCategory.SystemCritical or ProcessCategory.SecuritySoftware
            or ProcessCategory.AntiCheat or ProcessCategory.Driver or ProcessCategory.Unknown) return false;

        if (p.Pid == foregroundPid || p.AudioActive || audioPids.Contains(p.Pid)) return false;

        if (p.IsWhitelisted) return false;

        if (whitelist.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                               (!string.IsNullOrEmpty(p.ExecutablePath) &&
                                p.ExecutablePath.Contains(w, StringComparison.OrdinalIgnoreCase)))) return false;

        if (p.StartTime != DateTime.MinValue && (nowLocal - p.StartTime).TotalSeconds < minAgeSeconds)
            return false;

        return true;
    }
}
