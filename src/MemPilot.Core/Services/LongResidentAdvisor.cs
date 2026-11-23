using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 长驻未用建议（v0.21.0，默认关闭）。
///
/// 与 <see cref="JunkAdvisor"/> 的分工：JunkAdvisor 处理"有垃圾证据"的进程（内置名单 /
/// 用户标记），本类处理**没有垃圾证据、但确实长期占内存**的进程——11-23 日志复盘里的
/// vmmemWSL / ArmouryCrate / MSPC 就属于这一类：它们不是垃圾，杀掉会破坏功能，
/// 但它们跑了一整天、一次都没被用过，挂起 2/8 小时几乎是零成本的。
///
/// 因此动作只有「挂起」：卡片不提供"结束进程"（见 <see cref="JunkAdviceKind.LongResident"/>）。
///
/// 判定（全部满足）：
/// ① 运行 ≥ 8 小时（<see cref="MinResidentAge"/>）；
/// ② 私有提交 ≥ 512 MB（<see cref="MinPrivateBytes"/>，小进程不值得打扰）；
/// ③ 此刻无可见窗口、不在前台、无声；
/// ④ 行为账本存在且**今天没有任何活跃记录**（无账本不猜——刚启动的进程会被误报）；
/// ⑤ 通过 <see cref="JunkSafetyFilter"/>（受保护/系统/服务/危险类别/白名单全部排除）；
/// ⑥ 不在垃圾名单里（那是 JunkAdvisor 的活，避免同一进程被两处建议）。
///
/// 已知取舍：<see cref="ProcessCategory.Unknown"/> 被 JunkSafetyFilter 排除，所以
/// 未识别的长驻进程不会被建议。挂起 Unknown 进程的收益不足以抵消风险（可能是
/// 驱动辅助/系统组件），宁可漏报。
/// </summary>
public static class LongResidentAdvisor
{
    /// <summary>已运行不足该时长不算"长驻"。</summary>
    public static readonly TimeSpan MinResidentAge = TimeSpan.FromHours(8);

    /// <summary>低于该私有提交量不值得打扰（512 MB）。</summary>
    public const long MinPrivateBytes = 512L * 1024 * 1024;

    /// <summary>一次建议最多列出的进程数。</summary>
    public const int MaxCandidates = 3;

    public static JunkAdvice? Recommend(
        List<ProcessInfo> procs,
        IReadOnlyDictionary<string, ProcessBehavior> behaviors,
        int foregroundPid,
        HashSet<int> audioPids,
        IReadOnlyCollection<string> whitelist,
        JunkAdviceStore? adviceStore,
        DateTime nowUtc)
    {
        var nowLocal = nowUtc.ToLocalTime();
        var dayStartUtc = nowLocal.Date.ToUniversalTime();
        int minAgeSeconds = (int)MinResidentAge.TotalSeconds;

        var runningNames = new HashSet<string>(procs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var picked = new List<ProcessInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in procs)
        {
            if (p.HasVisibleWindow) continue;
            if (Math.Max(p.Memory.PrivateBytes, p.Memory.PrivateWorkingSetBytes) < MinPrivateBytes) continue;

            // ① 安全过滤（含 8h 最小存活期，复用唯一实现）
            if (!JunkSafetyFilter.IsEligible(p, foregroundPid, audioPids, whitelist, minAgeSeconds, nowLocal))
                continue;

            // ② 挂起会破坏子系统：只提示不挂起（WSL/Hyper-V 的 vmmem 一族）
            if (IsSuspendUnsafe(p.Name)) continue;

            // ③ 垃圾名单与孤儿残留归 JunkAdvisor / JunkSweeper
            if (p.Category == ProcessCategory.Bloatware) continue;
            if (ResidualDetector.FindOrphanHost(p.Name, runningNames) != null) continue;

            // ④ 账本必须有"今天没活跃过"的证据；无账本不猜
            if (!behaviors.TryGetValue(p.Name, out var b)) continue;
            if (b.WasActiveOn(dayStartUtc)) continue;

            // ⑤ 学习冷却（与垃圾建议共用账本：忽略过就不再骚扰）
            if (adviceStore != null && !adviceStore.ShouldSuggest(p.Name, nowUtc)) continue;

            if (!seen.Add(p.Name)) continue;   // 同名多 PID 只收一次
            picked.Add(p);
        }

        if (picked.Count == 0) return null;

        var top = picked
            .OrderByDescending(p => p.Memory.PrivateBytes)
            .Take(MaxCandidates)
            .ToList();

        return new JunkAdvice
        {
            Kind = JunkAdviceKind.LongResident,
            Candidates = top,
            TotalPrivateBytes = top.Sum(p => p.Memory.PrivateBytes),
            NamesText = string.Join("、", top.Select(p => p.Name))
                        + (picked.Count > top.Count ? $" 等 {picked.Count} 个" : ""),
            Reason = $"长驻未用的高占用进程 {picked.Count} 个"
                     + $"（已运行 ≥{MinResidentAge.TotalHours:F0} 小时、今天无窗口/音频/CPU 活动、私有提交 ≥{MinPrivateBytes / 1048576} MB）",
        };
    }

    /// <summary>
    /// 挂起会导致子系统冻结/失去响应的进程名（vmmem 是 WSL/Hyper-V 的虚拟机内存宿主，
    /// NtSuspendProcess 会让整个 WSL 停摆）。这类需要的是"关掉 WSL"，不是挂起。
    /// </summary>
    public static bool IsSuspendUnsafe(string name)
        => name.Contains("vmmem", StringComparison.OrdinalIgnoreCase);
}
