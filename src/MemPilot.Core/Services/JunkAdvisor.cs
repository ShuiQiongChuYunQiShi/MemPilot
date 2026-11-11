using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>一次垃圾进程建议（v0.15.0，v0.17.0 补充来源信息）。</summary>
public class JunkAdvice
{
    public List<ProcessInfo> Candidates { get; init; } = new();
    public long TotalPrivateBytes { get; init; }
    public string Reason { get; init; } = "";
    public string NamesText { get; init; } = "";
    /// <summary>来自用户标记「可清理」的数量（v0.17.0：文案要能区分来源）。</summary>
    public int MarkedCount { get; init; }
}

/// <summary>
/// 垃圾进程建议（v0.15.0）：找出"整天没被用过"的垃圾进程，交给用户决定
/// 挂起 2h / 8h 还是结束。**只建议，绝不自动执行**。
///
/// 数据依据（第四轮复盘）：
/// - 安全过滤（受保护/系统/服务/前台/发声/白名单/最小存活期）必须整体继承——
///   v0.17.0 起与 JunkSweeper 共用 <see cref="JunkSafetyFilter"/> 单一实现。
/// - HabitHint 111 次零响应的教训：单向提示无效，所以配 JunkAdviceStore 做反馈学习。
/// - v0.17.0：**当前有可见窗口的进程一律不推荐**——实测教训是"整天空开的窗口
///   被判成今天未使用"，差一点建议关闭用户正在用的窗口。
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
        // ① 安全候选：复用 JunkSweeper（它内部走 JunkSafetyFilter 单源）
        var junk = JunkSweeper.BuildPlan(procs, foregroundPid, audioPids, whitelist, minAgeSeconds);
        // 注意：junk 为空不能提前返回——下面还有「用户标记可清理」独立通道

        var nowLocal = nowUtc.ToLocalTime();
        var dayStartUtc = nowLocal.Date.ToUniversalTime();   // 本地今天 00:00 对应的 UTC

        var picked = new List<ProcessInfo>();
        var pickedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in junk)
        {
            var p = item.Process;
            if (p.HasVisibleWindow) continue;                // v0.17.0：此刻有窗口 = 别推荐

            // 今天用过没有？（行为账本：LastActiveUtc 是否落在今天）
            if (behaviors.TryGetValue(p.Name, out var b) && b.LastActiveUtc >= dayStartUtc)
                continue;

            if (!OlderThanMinResident(p, nowUtc)) continue;  // 已运行 ≥2h
            if (adviceStore != null && !adviceStore.ShouldSuggest(p.Name, nowUtc)) continue;

            picked.Add(p);
            pickedNames.Add(p.Name);
        }

        // ② 用户右键标记「可清理」的进程独立纳入（不在内置垃圾名单里）。
        // v0.17.0：安全过滤与 JunkSweeper 共用 JunkSafetyFilter，不再"两处复制靠注释同步"。
        var markedPicked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var marked = markedCleanable ?? Array.Empty<string>();
        foreach (var p in procs)
        {
            if (!marked.Any(m => string.Equals(m, p.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (pickedNames.Contains(p.Name)) continue;      // 同名多 PID 只收一次
            if (!JunkSafetyFilter.IsEligible(p, foregroundPid, audioPids, whitelist, minAgeSeconds, nowLocal))
                continue;
            if (p.HasVisibleWindow) continue;
            if (behaviors.TryGetValue(p.Name, out var mb) && mb.LastActiveUtc >= dayStartUtc) continue;
            if (!OlderThanMinResident(p, nowUtc)) continue;
            if (adviceStore != null && !adviceStore.ShouldSuggest(p.Name, nowUtc)) continue;

            picked.Add(p);
            pickedNames.Add(p.Name);
            markedPicked.Add(p.Name);
        }
        if (picked.Count == 0) return null;

        var top = picked
            .OrderByDescending(p => p.Memory.PrivateBytes)
            .Take(MaxCandidates)
            .ToList();

        long total = top.Sum(p => p.Memory.PrivateBytes);
        string names = string.Join("、", top.Select(p =>
            markedPicked.Contains(p.Name) ? $"{p.Name}（你标记的）" : p.Name));
        if (picked.Count > top.Count) names += $" 等 {picked.Count} 个";

        int markedInTop = top.Count(p => markedPicked.Contains(p.Name));
        return new JunkAdvice
        {
            Candidates = top,
            TotalPrivateBytes = total,
            NamesText = names,
            MarkedCount = markedInTop,
            Reason = $"整天未使用的垃圾进程 {picked.Count} 个（今日无窗口/音频/CPU 活动"
                     + (markedInTop > 0 ? $"，其中 {markedInTop} 个来自你的「可清理」标记" : "") + "）"
        };
    }

    /// <summary>已运行时长 ≥ MinResidentAge（启动时间不可读时按"够久"处理）。</summary>
    private static bool OlderThanMinResident(ProcessInfo p, DateTime nowUtc)
        => p.StartTime == DateTime.MinValue
           || nowUtc - p.StartTime.ToUniversalTime() >= MinResidentAge;
}
