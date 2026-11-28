namespace MemPilot.Services;

public enum AiSessionEvent { None, Started, Ended }

/// <summary>一次 AI 生成会话的摘要（开始/结束各产出一次，结束时最完整）。</summary>
public sealed class AiSessionSummary
{
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public long PeakUsedBytes { get; set; }
    public long PeakSharedBytes { get; set; }
    public double? PeakCommitFreePct { get; set; }
    public string DominantName { get; set; } = "";
    public long DominantPeakBytes { get; set; }
    public int PidCount { get; set; }              // 同名主导进程出现过的不同 PID 数（生成任务常每次换 PID）
    public TimeSpan Duration => (EndUtc ?? DateTime.UtcNow) - StartUtc;
}

public sealed class AiSessionUpdate
{
    public AiSessionUpdate(AiSessionEvent ev, AiSessionSummary? summary)
    { Event = ev; Summary = summary; }
    public AiSessionEvent Event { get; }
    public AiSessionSummary? Summary { get; }
}

/// <summary>
/// AI 生成会话识别（v0.5.0，纯状态机，30 秒显存采样驱动）。
///
/// 数据依据（11-01 真实曲线）：
/// - python 生成任务每次爆发换新 PID（20 个 PID / 18 次切换），单段 2–4 分钟，峰值专用 15.2 GB；
/// - 爆发之间存在单点回落（02:13:25 一条 0.76 GB，30 秒后 11.93 GB）→ 简单阈值会把手抖当结束；
/// - DeltaForce 稳定占 5–6.36 GB → 阈值必须高于它。
///
/// 因此采用 N-of-M + 滞回：最近 5 个样本里 ≥3 个 ≥8 GB 才算开始；≥3 个 &lt;4 GB 才算结束。
///
/// **检测器本身只观测与记录**（不处置、不切模式）。v0.25.0 起，VM 可以在这个信号上挂一个
/// **用户显式开启**的动作：申请 soft 租约（设置页「为 AI 任务自动预留」，默认关）。
/// soft 只清备用列表 + 压缩空闲后台，不改变任何进程状态——所以"自动"仍停在可逆范围内。
/// 硬租约（会挂起进程）**不会**被自动触发，这是刻意的。
/// </summary>
public sealed class AiSessionDetector
{
    public const long StartThresholdBytes = 8L * 1024 * 1024 * 1024;   // 8 GB
    public const long EndThresholdBytes = 4L * 1024 * 1024 * 1024;     // 4 GB

    private readonly int _window;
    private readonly int _quorum;
    private readonly Queue<long> _used = new();
    private AiSessionSummary? _current;

    public bool Active => _current != null;
    public AiSessionSummary? Current => _current;

    // ---- 会话状态持久化（v0.13.0，四轮复盘）----
    // 实测：11 个"开始"无"结束"全部是重启 artifact——检测器单槽正确，
    // 但进程重启把 _current 清零，同一个 312 分钟会话被劈成两段。
    // GpuMonitor 在会话开始/结束时调用 Save/Clear；启动时 TryRestore 续上未闭合会话。

    /// <summary>导出当前会话（无活跃会话返回 null）。JSON 形状供 GpuMonitor 落盘。</summary>
    public AiSessionSummary? ExportState() => _current is null ? null : Clone(_current);

    /// <summary>
    /// 恢复一个未闭合会话（应用重启后调用）。恢复的是"会话本体"——开始时间/峰值保持原值，
    /// 使下一段结束判定能闭合**原始**会话。已处于活跃会话时拒绝（幂等）。
    /// </summary>
    public bool TryRestore(AiSessionSummary persisted)
    {
        if (_current != null || persisted.EndUtc != null) return false;
        _current = Clone(persisted);
        _used.Clear();   // 重启后窗口无意义，重新攒结束判定的样本
        return true;
    }

    public AiSessionDetector(int window = 5, int quorum = 3)
    {
        _window = Math.Max(3, window);
        _quorum = Math.Clamp(quorum, 2, _window);
    }

    /// <summary>喂一个采样。usedBytes 为 null 表示当时采集不可用（按 0 计入结束判定，不更新峰值）。</summary>
    public AiSessionUpdate Push(long? usedBytes, long totalBytes, DateTime utc,
        IReadOnlyList<GpuSampleProc>? top = null, double? commitFreePct = null)
    {
        long used = usedBytes is > 0 ? usedBytes.Value : 0;
        _used.Enqueue(used);
        while (_used.Count > _window) _used.Dequeue();

        int high = _used.Count(v => v >= StartThresholdBytes);
        int low = _used.Count(v => v < EndThresholdBytes);

        if (_current == null)
        {
            // v0.9.0：重入保护——真机出现同一任务记了 3 次"开始"才等到 1 次"结束"，
            // 因为采样窗口被清空后会重新攒够 quorum。这里加一个"结束后冷却窗口"，
            // 避免刚结束就立刻再开一段（同一个任务被切成多段）。
            if (_lastEndUtc.HasValue && (utc - _lastEndUtc.Value).TotalMinutes < CooldownMinutes)
                return new AiSessionUpdate(AiSessionEvent.None, null);
            if (_used.Count < _quorum || high < _quorum) return new AiSessionUpdate(AiSessionEvent.None, null);
            _current = new AiSessionSummary
            {
                StartUtc = utc,
                PeakUsedBytes = used,
                PeakCommitFreePct = commitFreePct
            };
            Absorb(top);
            return new AiSessionUpdate(AiSessionEvent.Started, Clone(_current));
        }

        // 会话进行中：更新峰值/主导进程
        if (usedBytes is > 0)
        {
            _current.PeakUsedBytes = Math.Max(_current.PeakUsedBytes, usedBytes.Value);
            if (commitFreePct is { } cf)
                _current.PeakCommitFreePct = _current.PeakCommitFreePct is { } prev ? Math.Min(prev, cf) : cf;
        }
        Absorb(top);
        if (_current.PidCount > 512) _current.PidCount = 512;

        if (low >= _quorum)
        {
            _current.EndUtc = utc;
            var done = Clone(_current);
            _current = null;
            _lastEndUtc = utc;
            _used.Clear(); // 结束后重新攒窗口，避免残留高位样本立刻触发新会话
            return new AiSessionUpdate(AiSessionEvent.Ended, done);
        }

        return new AiSessionUpdate(AiSessionEvent.None, null);
    }

    private readonly HashSet<int> _dominantPids = new();
    private DateTime? _lastEndUtc;
    /// <summary>结束后冷却：这段时间内不再开新会话（防同一任务被切成多段）。</summary>
    public const int CooldownMinutes = 5;

    private void Absorb(IReadOnlyList<GpuSampleProc>? top)
    {
        if (_current == null || top == null || top.Count == 0) return;
        // 主导 = 专用显存最大的那条；共享显存只在同一采样内累加
        long shared = 0;
        GpuSampleProc? best = null;
        foreach (var p in top)
        {
            shared += Math.Max(0, p.S);
            if (best == null || p.D > best.D) best = p;
        }
        _current.PeakSharedBytes = Math.Max(_current.PeakSharedBytes, shared);

        if (best == null) return;
        if (string.IsNullOrEmpty(_current.DominantName) || best.D > _current.DominantPeakBytes)
        {
            _current.DominantName = best.N;
            _current.DominantPeakBytes = Math.Max(_current.DominantPeakBytes, best.D);
            _dominantPids.Clear();
            if (best.I > 0) _dominantPids.Add(best.I);
        }
        else if (string.Equals(best.N, _current.DominantName, StringComparison.OrdinalIgnoreCase))
        {
            if (best.I > 0) _dominantPids.Add(best.I);
        }
        _current.PidCount = _dominantPids.Count;
    }

    private static AiSessionSummary Clone(AiSessionSummary s) => new()
    {
        StartUtc = s.StartUtc,
        EndUtc = s.EndUtc,
        PeakUsedBytes = s.PeakUsedBytes,
        PeakSharedBytes = s.PeakSharedBytes,
        PeakCommitFreePct = s.PeakCommitFreePct,
        DominantName = s.DominantName,
        DominantPeakBytes = s.DominantPeakBytes,
        PidCount = s.PidCount
    };
}
