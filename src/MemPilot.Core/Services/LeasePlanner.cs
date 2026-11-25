using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>plan 里一个"将会被挂起"的进程（只读快照，不动作）。</summary>
public sealed class LeasePlanSuspend
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public long PrivateBytes { get; set; }
}

/// <summary>
/// 被安全边界挡住的进程统计（v0.23.0）。
/// 存在的意义：让"L4 永不自动 / Unknown 永不自动"从 README 里的一句话变成调用方看得见的数字。
/// </summary>
public sealed class LeasePlanExcluded
{
    public int Total { get; set; }
    public Dictionary<string, int> ByReason { get; set; } = new();
}

/// <summary>
/// 零动作预演结果（`POST /api/plan`）。
///
/// 为什么需要：agent 在起一个 12GB 任务之前想知道"能不能拿到"，而现在唯一的办法是真申请——
/// 真申请会真的挂起进程。plan 只读快照、只跑纯函数，不调用任何处置原语。
///
/// 诚实性约束：soft 策略下 trim/purge 的实际收益不可预测，所以 ProjectedAvailableGb 只报**下界**
/// （= 当前可用），并用 ProjectionBasis 告诉调用方这个数字是怎么来的。
/// </summary>
public sealed class LeasePlan
{
    public double TargetAvailableGb { get; set; }
    public double CurrentAvailableGb { get; set; }
    public double ProjectedAvailableGb { get; set; }
    public double ShortfallGb { get; set; }
    public bool Feasible { get; set; }
    public string Policy { get; set; } = "soft";
    /// <summary>
    /// `hard-upper-bound`：projected = 当前 + 将要挂起的私有提交合计（乐观上界，实际释放 ≤ 此值）。
    /// `current-only`：不动任何进程就有这么多（soft 的下界，或 hard 已达标）。
    /// </summary>
    public string ProjectionBasis { get; set; } = "current-only";
    /// <summary>快照年龄（秒）。plan 基于上一次 tick 的进程快照，不是实时枚举——过期的快照会低估可用量。</summary>
    public double SnapshotAgeSeconds { get; set; }
    public List<LeasePlanSuspend> WouldSuspend { get; set; } = new();
    public LeasePlanExcluded Excluded { get; set; } = new();
}

/// <summary>
/// 租约预演（v0.23.0 MCP/agent 接入）：复用调度器的候选选择，不做任何动作。
///
/// 为什么单独一个类而不是塞进 LeaseManager：LeaseManager 是状态机（有账本、有锁、有恢复钩子），
/// plan 是无状态纯函数。混进去会让"预演"有机会碰到真实状态——那就不再是零动作了。
/// </summary>
public static class LeasePlanner
{
    private static readonly HashSet<int> EmptyAudio = new();
    private const double Gb = 1073741824.0;

    public static LeasePlan Build(
        IEnumerable<ProcessInfo>? procs, int foregroundPid, IReadOnlySet<int>? audioPids,
        DateTime now, double targetAvailableGb, LeasePolicy policy, double currentAvailableGb,
        double snapshotAgeSeconds, IReadOnlySet<int>? excludedPids = null)
    {
        var plan = new LeasePlan
        {
            TargetAvailableGb = Math.Round(Math.Max(0, targetAvailableGb), 2),
            CurrentAvailableGb = Math.Round(Math.Max(0, currentAvailableGb), 2),
            Policy = policy == LeasePolicy.Hard ? "hard" : "soft",
            // -1 = 还没有进程快照（刚启动）。保留负值，别钳成 0 —— 0 会被误读成"刚刚枚举过"。
            SnapshotAgeSeconds = snapshotAgeSeconds < 0 ? -1 : Math.Round(snapshotAgeSeconds, 1),
        };

        var list = procs as IList<ProcessInfo> ?? procs?.ToList() ?? new List<ProcessInfo>();
        var audio = audioPids ?? EmptyAudio;

        // 被安全边界挡住多少：这是"excluded.byReason.unknown > 0"能成为可见证据的地方
        var byReason = new Dictionary<string, int>();
        foreach (var p in list)
        {
            var reject = TrimSelection.ExplainUnsafe(p, foregroundPid, audio, now, excludedPids);
            if (reject == TrimSelection.TrimReject.None) continue;
            var key = ReasonKey(reject);
            byReason[key] = byReason.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        plan.Excluded = new LeasePlanExcluded { Total = byReason.Values.Sum(), ByReason = byReason };

        double projected = Math.Max(0, currentAvailableGb);

        if (policy == LeasePolicy.Hard && targetAvailableGb > currentAvailableGb)
        {
            long needBytes = (long)((targetAvailableGb - currentAvailableGb) * Gb);
            var candidates = LeaseHardPlanner.PickSuspendCandidates(list, foregroundPid, audio, now, excludedPids);
            var picked = LeaseHardPlanner.TakeForShortfall(candidates, needBytes);
            foreach (var p in picked)
            {
                long priv = Math.Max(0, p.Memory.PrivateBytes);
                plan.WouldSuspend.Add(new LeasePlanSuspend { Pid = p.Pid, Name = p.Name, PrivateBytes = priv });
                projected += priv / Gb;
            }
            plan.ProjectionBasis = "hard-upper-bound";
        }

        plan.ProjectedAvailableGb = Math.Round(projected, 2);
        plan.ShortfallGb = Math.Round(Math.Max(0, targetAvailableGb - plan.ProjectedAvailableGb), 2);
        plan.Feasible = plan.ProjectedAvailableGb >= targetAvailableGb - 1e-9;
        return plan;
    }

    /// <summary>拒绝原因的稳定英文 key（对外 JSON 用，不随界面语言变）。</summary>
    public static string ReasonKey(TrimSelection.TrimReject reject) => reject switch
    {
        TrimSelection.TrimReject.Caller => "caller",
        TrimSelection.TrimReject.Self => "self",
        TrimSelection.TrimReject.Protected => "protected",
        TrimSelection.TrimReject.Category => "category",
        TrimSelection.TrimReject.Workload => "workload",
        TrimSelection.TrimReject.InUse => "inUse",
        _ => "invalid",
    };
}
