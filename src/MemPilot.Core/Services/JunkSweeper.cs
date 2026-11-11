using MemPilot.Models;

namespace MemPilot.Services;

public record JunkSweepItem(ProcessInfo Process, CleanupActionLevel Action, string Reason, string Evidence);

/// <summary>
/// 垃圾驻守清扫器：对 Bloatware 分类进程与"宿主已退出的孤儿残留"做直接处置的计划。
/// 安全边界（硬编码，不随配置放宽）：
///  - 受保护 / 系统关键 / 安全软件 / 反作弊 / 驱动 / 未识别：永不触碰；
///  - Windows 服务（IsService）不杀：SCM 会自动重启，杀了只是抖动（这类需手动禁用服务）；
///  - 有可见窗口的不杀（用户可能正在编辑），只 L1 回收工作集；
///  - 前台进程、有声进程、存活不足 MinProcessAgeSeconds、白名单：不碰。
/// 决策日志记 Kind=JunkSweep，自动模式可在设置里开关（默认关）。
/// </summary>
public static class JunkSweeper
{
    public static List<JunkSweepItem> BuildPlan(
        List<ProcessInfo> procs, int foregroundPid, HashSet<int> audioPids,
        IReadOnlyCollection<string> whitelist, int minAgeSeconds, bool orphansOnly = false)
    {
        var runningNames = new HashSet<string>(procs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var plan = new List<JunkSweepItem>();
        var now = DateTime.Now;

        foreach (var p in procs)
        {
            // v0.17.0：七条安全过滤收敛到 JunkSafetyFilter（与 JunkAdvisor 共用一份）
            if (!JunkSafetyFilter.IsEligible(p, foregroundPid, audioPids, whitelist, minAgeSeconds, now))
                continue;

            string reason;
            string evidence;
            if (p.Category == ProcessCategory.Bloatware)
            {
                if (orphansOnly) continue; // 低风险模式：只清宿主已退出的孤儿残留
                reason = "垃圾常驻";
                // P3.1 证据分级：junk_vendor（可信签名厂商）> junk_name（精确名名单）
                evidence = p.CategoryReason == "junk_vendor" ? "vendor-signature" : "exact-name";
            }
            else
            {
                var host = ResidualDetector.FindOrphanHost(p.Name, runningNames);
                if (host == null) continue;
                reason = $"残留服务（宿主 {host} 已退出）";
                evidence = "host-relation";
            }

            // v0.9.0：自动清扫**永不出 L4**——旧实现对无窗口垃圾直接强杀，
            // 而 UI 的「低风险模式」文案写的是"不会结束进程"，两者自相矛盾（三轮复盘 P0）。
            // v0.13.0（四轮复盘，门控反转修正）：**有窗口 → L3，无窗口 → L1**。
            // 旧写法正好相反：WM_CLOSE 只对有窗口进程有意义；实测 11-05 对无窗口的
            // wps/wetype_update/lghub_updater 优雅关闭全部 8 秒超时（Exited:false）。
            // CleanupEngine.BuildPlan:256-267 一直是正确版本，两份门控在此收敛。
            if (p.HasVisibleWindow)
                plan.Add(new JunkSweepItem(p, CleanupActionLevel.L3GracefulClose, reason + "；有窗口，优雅关闭（不强制结束）", evidence));
            else
                plan.Add(new JunkSweepItem(p, CleanupActionLevel.L1Trim, reason + "；无窗口（WM_CLOSE 无效），仅回收工作集", evidence));
        }
        return plan;
    }
}
