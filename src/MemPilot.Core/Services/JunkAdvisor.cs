using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>一次垃圾进程建议（v0.15.0）。</summary>
public class JunkAdvice
{
    public List<ProcessInfo> Candidates { get; init; } = new();
    public long TotalPrivateBytes { get; init; }
    public string Reason { get; init; } = "";
    public string NamesText { get; init; } = "";
}

/// <summary>
/// 垃圾进程建议（v0.15.0）：找出"整天没被用过"的垃圾进程，交给用户决定
/// 挂起 2h / 8h 还是结束。**只建议，绝不自动执行**。
///
/// 数据依据（第四轮复盘）：
/// - JunkSweeper 的安全过滤（受保护/系统/服务/前台/发声/白名单/最小存活期）必须整体继承——
///   本类不复写任何安全判定，只在其输出上追加"今日未使用"这一条。
/// - HabitHint 111 次零响应的教训：单向提示无效，所以配 JunkAdviceStore 做反馈学习。
/// </summary>
public static class JunkAdvisor
{
    /// <summary>已运行不足该时长的进程不提示（避免骚扰刚启动的更新器）。</summary>
    public static readonly TimeSpan MinResidentAge = TimeSpan.FromHours(2);

    /// <summary>一次建议最多列出的进程数。</summary>
    public const int MaxCandidates = 5;

    /// <summary>
    /// 计算一次建议。返回 null 表示没有值得提示的对象。
    /// </summary>
    /// <param name="procs">当前进程快照</param>
    /// <param name="behaviors">行为账本（进程名 → 行为；用于"今日是否活跃"）</param>
    /// <param name="foregroundPid">前台 PID（沿用既有前台豁免）</param>
    /// <param name="audioPids">正在发声的 PID 集合</param>
    /// <param name="whitelist">用户白名单</param>
    /// <param name="minAgeSeconds">最小存活期（沿用配置）</param>
    /// <param name="adviceStore">学习账本（可为 null，表示不做冷却过滤）</param>
    /// <param name="nowUtc">当前 UTC（可注入以便测试）</param>
    public static JunkAdvice? Recommend(
        List<ProcessInfo> procs,
        IReadOnlyDictionary<string, ProcessBehavior> behaviors,
        int foregroundPid,
        HashSet<int> audioPids,
        IReadOnlyCollection<string> whitelist,
        int minAgeSeconds,
        JunkAdviceStore? adviceStore,
        DateTime nowUtc,
        IReadOnlyCollection<string>? markedCleanable = null)
    {
        // ① 安全候选：完全复用 JunkSweeper（不复制任何安全判定）
        var junk = JunkSweeper.BuildPlan(procs, foregroundPid, audioPids, whitelist, minAgeSeconds);
        // 注意：junk 为空不能提前返回——下面还有「用户标记可清理」独立通道

        var nowLocal = nowUtc.ToLocalTime();
        var dayStartUtc = nowLocal.Date.ToUniversalTime();   // 本地今天 00:00 对应的 UTC

        var picked = new List<ProcessInfo>();
        foreach (var item in junk)
        {
            var p = item.Process;

            // ② 今天用过没有？（行为账本：LastActiveUtc 是否落在今天）
            if (behaviors.TryGetValue(p.Name, out var b) && b.LastActiveUtc >= dayStartUtc)
                continue;   // 今天活跃过 → 不打扰

            // ③ 已运行足够久（启动时间不可读时按"够久"处理，因为 JunkSweeper 已过了最小存活期）
            if (p.StartTime != DateTime.MinValue && nowUtc - p.StartTime.ToUniversalTime() < MinResidentAge)
                continue;

            // ④ 学习冷却（用户反复忽略的就不再打扰）
            if (adviceStore != null && !adviceStore.ShouldSuggest(p.Name, nowUtc))
                continue;

            picked.Add(p);
        }

        // v0.15.1：用户右键标记过「可清理」的进程独立纳入（它们不在内置垃圾名单里，
        // BuildPlan 抓不到）。安全过滤逐条复制自 JunkSweeper.BuildPlan——两处必须同步，
        // 改一处必须改另一处（有守卫测试锁定）。
        var pickedNames = picked.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var marked = markedCleanable ?? Array.Empty<string>();
        foreach (var p in procs)
        {
            if (!marked.Any(m => string.Equals(m, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (pickedNames.Contains(p.Name)) continue;
            if (p.IsProtected || p.IsSystemProcess || p.IsService) continue;
            if (p.Category is ProcessCategory.SystemCritical or ProcessCategory.SecuritySoftware
                or ProcessCategory.AntiCheat or ProcessCategory.Driver or ProcessCategory.Unknown) continue;
            if (p.Pid == foregroundPid || p.AudioActive || audioPids.Contains(p.Pid)) continue;
            if (p.IsWhitelisted) continue;
            if (whitelist.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                   (!string.IsNullOrEmpty(p.ExecutablePath) &&
                                    p.ExecutablePath.Contains(w, StringComparison.OrdinalIgnoreCase)))) continue;
            if (p.StartTime != DateTime.MinValue && nowUtc.ToLocalTime() - p.StartTime < TimeSpan.FromSeconds(minAgeSeconds)) continue;
            if (behaviors.TryGetValue(p.Name, out var mb) && mb.LastActiveUtc >= dayStartUtc) continue;
            if (p.StartTime != DateTime.MinValue && nowUtc - p.StartTime.ToUniversalTime() < MinResidentAge) continue;
            if (adviceStore != null && !adviceStore.ShouldSuggest(p.Name, nowUtc)) continue;
            picked.Add(p);
        }
        if (picked.Count == 0) return null;

        var top = picked
            .OrderByDescending(p => p.Memory.PrivateBytes)
            .Take(MaxCandidates)
            .ToList();

        long total = top.Sum(p => p.Memory.PrivateBytes);
        string names = string.Join("、", top.Select(p => p.Name));
        if (picked.Count > top.Count) names += $" 等 {picked.Count} 个";

        return new JunkAdvice
        {
            Candidates = top,
            TotalPrivateBytes = total,
            NamesText = names,
            Reason = $"整天未使用的垃圾进程 {picked.Count} 个（今日无窗口/音频/CPU 活动）"
        };
    }
}
