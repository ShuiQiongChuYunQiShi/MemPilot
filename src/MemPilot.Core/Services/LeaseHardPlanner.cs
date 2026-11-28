using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>硬租约策略：soft 只压缩；hard 允许在"安全子集"里挂起进程（可恢复）。</summary>
public enum LeasePolicy
{
    Soft,
    Hard,
}

/// <summary>一次硬租约里被挂起的进程（归还时逐个恢复；PID+StartTime 防复用）。</summary>
public sealed class LeaseSuspendedProcess
{
    public int Pid { get; set; }
    public DateTime StartTime { get; set; }
    public string Name { get; set; } = "";
    public long PrivateBytes { get; set; }
    public bool Restored { get; set; }
    public string RestoreNote { get; set; } = "";
}

/// <summary>
/// 硬租约的挂起选择与恢复编排（v0.8.0 M2，纯函数 + 状态）。
///
/// 为什么需要：软租约（压缩工作集）只能把页面挪到页面文件，对"提交内存"和"真的需要物理内存"的
/// 大任务帮不上——实测压缩后提交余量不变。硬租约用 L2 挂起真正冻结进程并让出物理内存，
/// 归还时再恢复，这是"给工作让路"最直接的兑现方式。
///
/// 安全边界（比软租约更严）：只在 TrimSelection 的安全子集里挑；额外要求"足够闲"——
/// 无窗口 或 空闲 ≥10 分钟，避免挂起用户正在看但没在操作的程序。用户明确不要工作进程被回收，
/// 所以 Work/AI/娱乐用途、游戏、前台、发声、受保护的进程一律不进候选。
/// </summary>
public static class LeaseHardPlanner
{
    /// <summary>挂起的额外门槛：空闲至少这么久（配合"无窗口或已空闲"双条件）。</summary>
    public const double MinIdleMinutes = 10;

    /// <summary>
    /// 从当前快照挑出可挂起的候选，按可释放提交内存降序排列。
    /// 返回的列表供调用方按目标额度截断后实际挂起。
    /// <paramref name="excludedPids"/>：调用方自身及其子树（v0.23.0 硬不变式）。
    /// <paramref name="denyNames"/>：调用方点名的进程（精确名、忽略大小写）——这是**偏好**不是
    /// 安全规则，所以作用在候选集上，不进 TrimSelection。
    /// </summary>
    public static List<ProcessInfo> PickSuspendCandidates(
        IEnumerable<ProcessInfo> procs, int foregroundPid, IReadOnlySet<int> audioPids, DateTime now,
        IReadOnlySet<int>? excludedPids = null, IEnumerable<string>? denyNames = null)
    {
        var result = new List<ProcessInfo>();
        if (procs == null) return result;
        // 默认名单（挂起即断网的那一类）总是生效，再并入调用方点名的
        var deny = MergeDenyNames(denyNames);

        foreach (var p in procs)
        {
            if (!TrimSelection.IsSafe(p, foregroundPid, audioPids, now, excludedPids)) continue;
            if (!string.IsNullOrEmpty(p.Name) && deny.Contains(p.Name)) continue;
            // 额外：必须"足够闲"——无窗口，或已空闲 ≥ MinIdleMinutes
            bool idle = !p.HasVisibleWindow || p.IdleMinutes >= MinIdleMinutes;
            if (!idle) continue;
            result.Add(p);
        }

        return result
            .OrderByDescending(p => p.Memory.PrivateBytes)   // 挂起主要释放提交内存
            .ToList();
    }

    /// <summary>单次硬租约最多挂起几个——永不一次挂一片。单一来源：API 的 limits 也读它。</summary>
    public const int MaxSuspendCount = 8;

    /// <summary>
    /// 默认排除的进程名（v0.23.0）：**挂起即断网**的那一类。精确名、忽略大小写。
    ///
    /// 为什么必须默认排除：这些进程无窗口、空闲、体积够大，完美满足硬租约的挂起条件。
    /// 但挂起代理/VPN 内核会切断所有网络——包括调用方（agent）自己调 LLM API 的连接，
    /// 而 agent 看到的是"请求超时"，只会去重试，永远不会想到是内存工具干的。
    /// 这不是"腾内存腾多了"，是工具主动制造了它要解决的故障。
    ///
    /// 为什么放在这里而不是 TrimSelection：它是**默认偏好**不是**安全边界**——
    /// 安全边界回答"谁绝对不能动"，这份名单回答"默认别动谁"。调用方仍可通过
    /// denyNames 增补，也可以在被排除后自行决定不申请（但无法强行要求挂起它们）。
    ///
    /// 名单必然不全（新代理工具层出不穷）。漏了只是回到旧行为，不会更糟；
    /// 调用方发现漏网时用 denyNames 补。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDenyNames = new[]
    {
        // 代理 / 分流内核
        "FlClashCore", "FlClash", "clash", "clash-meta", "mihomo", "verge-mihomo",
        "sing-box", "v2ray", "xray", "hysteria", "trojan", "naive",
        // VPN / 远程访问
        "tailscaled", "wireguard", "wg", "openvpn", "zerotier-one", "netbird", "cloudflared",
        // 虚拟网卡 / 桥接
        "wintun", "tap-windows"
    };

    /// <summary>
    /// 合并"默认排除"与"调用方点名排除"。默认名单总是生效——传 null 也一样。
    /// 归一化（Trim / 去空 / 忽略大小写）在这里统一做，不假设调用方集合用了什么 comparer。
    /// </summary>
    public static HashSet<string> MergeDenyNames(IEnumerable<string>? callerDeny)
    {
        var set = new HashSet<string>(DefaultDenyNames, StringComparer.OrdinalIgnoreCase);
        if (callerDeny != null)
            foreach (var n in callerDeny)
                if (!string.IsNullOrWhiteSpace(n)) set.Add(n.Trim());
        return set;
    }

    /// <summary>按目标缺口截断候选：累计私有提交达到 needBytes 即停（最多 maxCount 个，避免一刀切挂一片）。</summary>
    public static List<ProcessInfo> TakeForShortfall(List<ProcessInfo> candidates, long needBytes,
        int maxCount = MaxSuspendCount)
    {
        var picked = new List<ProcessInfo>();
        if (candidates == null || needBytes <= 0) return picked;
        long sum = 0;
        foreach (var p in candidates)
        {
            if (picked.Count >= maxCount) break;
            picked.Add(p);
            sum += Math.Max(0, p.Memory.PrivateBytes);
            if (sum >= needBytes) break;
        }
        return picked;
    }
}
