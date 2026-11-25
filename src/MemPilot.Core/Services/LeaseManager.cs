using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>租约申请（AI 任务开跑前发出）。</summary>
public sealed class LeaseRequest
{
    public double TargetAvailableGb { get; set; } = 8;
    public int TtlSeconds { get; set; } = 3600;
    public string Requester { get; set; } = "";
    /// <summary>soft（默认，只压缩）/ hard（允许挂起安全子集，归还时恢复）。</summary>
    public string Policy { get; set; } = "soft";
    /// <summary>
    /// v0.23.0：调用方 PID（可选）。传入后该进程及其子树**永不进入处置候选**——
    /// agent 的 worker 通常是"无窗口 + 空闲"，恰好满足硬租约的挂起条件，
    /// 不排除就会把自己挂起然后一直等（见 ProcessTreeBuilder.BuildSubtreePids）。
    /// 0 = 未指定，行为与旧版一致。
    /// </summary>
    public int RequesterPid { get; set; }
}

/// <summary>租约状态：active → released / expired。软租约只压缩"安全子集"，没有需要恢复的东西。</summary>
public sealed class LeaseInfo
{
    public string Id { get; set; } = "";
    public double TargetAvailableGb { get; set; }
    public double AchievedAvailableGb { get; set; }
    public double BeforeAvailableGb { get; set; }
    public double AfterAvailableGb { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string State { get; set; } = "active";   // active / released / expired
    public string Requester { get; set; } = "";
    /// <summary>调用方 PID（0 = 未指定）。审计用，也用于核对"未处置调用方自身"。</summary>
    public int RequesterPid { get; set; }
    public string Details { get; set; } = "";
    public DateTime? EndUtc { get; set; }
    /// <summary>实际生效的策略：soft / hard。</summary>
    public string Policy { get; set; } = "soft";
    /// <summary>硬租约挂起的进程（归还时逐个恢复）。</summary>
    public List<LeaseSuspendedProcess> Suspended { get; set; } = new();
    public double ShortfallGb => Math.Max(0, TargetAvailableGb - AchievedAvailableGb);
}

/// <summary>软腾挪执行结果（由调用方注入：清备用列表 + 安全子集压缩，实测量）。</summary>
public sealed record LeaseReliefResult(double BeforeGb, double AfterGb, string Details);

/// <summary>
/// 单活跃租约状态机（v0.7.0 M1，纯逻辑）。
///
/// 语义照搬码头 `res lease`：申请 → 返回**实测达成量**（不是承诺量）；占用期守护（可用内存跌破
/// 授予值 1GB 以上 → 告警一次）；归还 = 主动 DELETE 或 TTL 到期。软租约只做 L1/备用列表，
/// 没有挂起需要恢复，所以"归还"只是记账与告警解除。
/// </summary>
public sealed class LeaseManager
{
    private readonly object _lock = new();
    private LeaseInfo? _current;
    private bool _guardAlerted;

    /// <summary>授予时承诺的"可用内存下限"——守护线 = AfterGb（实测值）- 1GB。</summary>
    public const double GuardErosionGb = 1.0;
    public const int MaxTtlSeconds = 24 * 3600;
    public const int DefaultTtlSeconds = 3600;

    /// <summary>租约事件（开始/结束/守护告警），由调用方接决策日志与横幅。</summary>
    public event Action<LeaseInfo, string>? LeaseEvent;

    /// <summary>
    /// 同步恢复钩子（v0.8.0）：硬租约结束**之前**必须把挂起的进程恢复回来，
    /// 且必须在 API 返回响应之前完成——否则调用方拿到的是"未恢复"的账本
    /// （实测：决策日志说恢复 8 个、HTTP 响应写 0 个，因为事件走的 UI 线程是异步的）。
    /// </summary>
    public Func<LeaseInfo, (int restored, int failed)>? RestoreHook { get; set; }

    /// <summary>当前活跃租约；TTL 到期自动转 expired 并发事件。</summary>
    public LeaseInfo? Current(DateTime nowUtc)
    {
        LeaseInfo? expired = null;
        lock (_lock)
        {
            if (_current == null) return null;
            if (_current.State == "active" && nowUtc >= _current.ExpiresUtc)
            {
                _current.State = "expired";
                _current.EndUtc = nowUtc;
                expired = _current;
            }
            else return _current.State == "active" ? _current : null;
        }
        // 锁外做恢复：恢复要结束/唤醒进程，不该占着租约锁
        FinishLease(expired!, "过期");
        return null;
    }

    /// <summary>统一收尾：先同步恢复挂起的进程，再发事件（顺序很重要，见 RestoreHook 注释）。</summary>
    private void FinishLease(LeaseInfo lease, string ev)
    {
        try { RestoreHook?.Invoke(lease); } catch { }
        LeaseEvent?.Invoke(lease, ev);
    }

    /// <summary>申请。已有活跃租约返回 null（单活跃模型，调用方映射 409）。</summary>
    public LeaseInfo? Acquire(LeaseRequest req, LeaseReliefResult relief, DateTime nowUtc,
        LeasePolicy policy = LeasePolicy.Soft, List<LeaseSuspendedProcess>? suspended = null)
    {
        if (req == null) return null;
        lock (_lock)
        {
            if (_current != null && _current.State == "active" && nowUtc < _current.ExpiresUtc) return null;
            var ttl = Math.Clamp(req.TtlSeconds <= 0 ? DefaultTtlSeconds : req.TtlSeconds, 30, MaxTtlSeconds);
            _current = new LeaseInfo
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                TargetAvailableGb = Math.Max(0.5, req.TargetAvailableGb),
                BeforeAvailableGb = relief.BeforeGb,
                AfterAvailableGb = relief.AfterGb,
                AchievedAvailableGb = relief.AfterGb,
                StartUtc = nowUtc,
                ExpiresUtc = nowUtc.AddSeconds(ttl),
                State = "active",
                Requester = req.Requester ?? "",
                RequesterPid = req.RequesterPid,
                Details = relief.Details,
                Policy = policy == LeasePolicy.Hard ? "hard" : "soft",
                Suspended = suspended ?? new List<LeaseSuspendedProcess>()
            };
            _guardAlerted = false;
            LeaseEvent?.Invoke(_current, "开始");
            return _current;
        }
    }

    private bool _reserving;

    /// <summary>
    /// 占位式申请（v0.9.0 修并发泄漏）：先原子占位，再让调用方去执行腾挪，最后回填结果。
    /// 旧实现是「检查 null → 执行 relief → Acquire」，两个并发 POST 都能通过检查，
    /// 第二个 Acquire 返回 null 但它的 relief 已经挂起了进程 → 那批进程不在任何账本里，永不恢复。
    /// </summary>
    public LeaseInfo? TryReserve(LeaseRequest req, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_reserving) return null;
            if (_current != null && _current.State == "active" && nowUtc < _current.ExpiresUtc) return null;
            _reserving = true;
            var ttl = Math.Clamp(req.TtlSeconds <= 0 ? DefaultTtlSeconds : req.TtlSeconds, 30, MaxTtlSeconds);
            _current = new LeaseInfo
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                TargetAvailableGb = Math.Max(0.5, req.TargetAvailableGb),
                StartUtc = nowUtc,
                ExpiresUtc = nowUtc.AddSeconds(ttl),
                State = "reserving",
                Requester = req.Requester ?? "",
                RequesterPid = req.RequesterPid,
                Details = "腾挪中…"
            };
            return _current;
        }
    }

    /// <summary>腾挪完成，回填实测结果并转 active。</summary>
    public LeaseInfo? CompleteReservation(string id, LeaseReliefResult relief, DateTime nowUtc,
        LeasePolicy policy, List<LeaseSuspendedProcess>? suspended)
    {
        lock (_lock)
        {
            _reserving = false;
            if (_current == null || !string.Equals(_current.Id, id, StringComparison.Ordinal)) return null;
            _current.State = "active";
            _current.BeforeAvailableGb = relief.BeforeGb;
            _current.AfterAvailableGb = relief.AfterGb;
            _current.AchievedAvailableGb = relief.AfterGb;
            _current.Details = relief.Details;
            _current.Policy = policy == LeasePolicy.Hard ? "hard" : "soft";
            _current.Suspended = suspended ?? new List<LeaseSuspendedProcess>();
            _guardAlerted = false;
            LeaseEvent?.Invoke(_current, "开始");
            return _current;
        }
    }

    /// <summary>腾挪失败/取消：释放占位。</summary>
    public void AbortReservation(string id)
    {
        lock (_lock)
        {
            _reserving = false;
            if (_current != null && string.Equals(_current.Id, id, StringComparison.Ordinal) &&
                _current.State == "reserving")
                _current = null;
        }
    }

    /// <summary>归还（主动 DELETE）。id 不匹配返回 null。</summary>
    public LeaseInfo? Release(string id, DateTime nowUtc, string reason = "归还")
    {
        LeaseInfo done;
        lock (_lock)
        {
            if (_current == null || _current.State != "active" ||
                !string.Equals(_current.Id, id, StringComparison.Ordinal)) return null;
            _current.State = "released";
            _current.EndUtc = nowUtc;
            done = _current;
        }
        FinishLease(done, reason);
        return done;
    }

    /// <summary>占用期守护：可用内存跌破授予值 - 1GB → 告警一次（不重复刷）。</summary>
    public LeaseInfo? CheckGuard(SystemMemoryInfo mem, DateTime nowUtc)
    {
        var cur = Current(nowUtc);
        if (cur == null) { _guardAlerted = false; return null; }
        double availGb = mem.AvailablePhysicalBytes / 1073741824.0;
        if (availGb < cur.AfterAvailableGb - GuardErosionGb && !_guardAlerted)
        {
            _guardAlerted = true;
            var copy = Clone(cur);
            copy.Details = $"租约被侵蚀：可用 {availGb:F1} GB < 授予下限 {cur.AfterAvailableGb - GuardErosionGb:F1} GB";
            LeaseEvent?.Invoke(copy, "守护告警");
            return copy;
        }
        return null;
    }

    private static LeaseInfo Clone(LeaseInfo s) => new()
    {
        Id = s.Id, TargetAvailableGb = s.TargetAvailableGb, AchievedAvailableGb = s.AchievedAvailableGb,
        BeforeAvailableGb = s.BeforeAvailableGb, AfterAvailableGb = s.AfterAvailableGb,
        StartUtc = s.StartUtc, ExpiresUtc = s.ExpiresUtc, State = s.State,
        Requester = s.Requester, RequesterPid = s.RequesterPid, Details = s.Details, EndUtc = s.EndUtc,
        Policy = s.Policy,
        // 深拷贝：归还/过期事件里恢复流程会改写 Restored/RestoreNote，
        // 如果共享同一个 list，API 返回的副本就看不到这些结果（v0.8.0 实测踩到：日志说恢复 8，接口说 0）
        Suspended = s.Suspended.Select(x => new LeaseSuspendedProcess
        {
            Pid = x.Pid, StartTime = x.StartTime, Name = x.Name,
            PrivateBytes = x.PrivateBytes, Restored = x.Restored, RestoreNote = x.RestoreNote
        }).ToList()
    };
}

/// <summary>租约 API 令牌的存取（明文落盘 lease-token.txt；仅本机可用，权限即用户目录）。</summary>
public static class LeaseTokenStore
{
    public static string PathFor(string dataDir) => Path.Combine(dataDir, "lease-token.txt");

    public static string LoadOrCreate(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            if (File.Exists(path))
            {
                var t = File.ReadAllText(path).Trim();
                if (t.Length >= 16) return t;
            }
            return Regenerate(dataDir);
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    public static string Regenerate(string dataDir)
    {
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];
        try { File.WriteAllText(PathFor(dataDir), token); } catch { }
        return token;
    }
}
