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
        IReadOnlyCollection<string> whitelist, int minAgeSeconds)
    {
        var runningNames = new HashSet<string>(procs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var plan = new List<JunkSweepItem>();
        var now = DateTime.Now;

        foreach (var p in procs)
        {
            if (p.IsProtected || p.IsSystemProcess || p.IsService) continue;
            if (p.Category is ProcessCategory.SystemCritical or ProcessCategory.SecuritySoftware
                or ProcessCategory.AntiCheat or ProcessCategory.Driver or ProcessCategory.Unknown) continue;
            if (p.Pid == foregroundPid || p.AudioActive || audioPids.Contains(p.Pid)) continue;
            if (p.IsWhitelisted) continue;
            if (whitelist.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                   (!string.IsNullOrEmpty(p.ExecutablePath) &&
                                    p.ExecutablePath.Contains(w, StringComparison.OrdinalIgnoreCase)))) continue;
            if (p.StartTime != DateTime.MinValue && (now - p.StartTime).TotalSeconds < minAgeSeconds) continue;

            string reason;
            string evidence;
            if (p.Category == ProcessCategory.Bloatware)
            {
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

            if (p.HasVisibleWindow)
                plan.Add(new JunkSweepItem(p, CleanupActionLevel.L1Trim, reason + "；有窗口，仅回收工作集", evidence));
            else
                plan.Add(new JunkSweepItem(p, CleanupActionLevel.L4ForceKill, reason + "；无窗口，直接结束", evidence));
        }
        return plan;
    }
}
