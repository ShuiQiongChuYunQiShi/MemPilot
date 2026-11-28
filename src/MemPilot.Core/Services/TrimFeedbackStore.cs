using System.IO;
using System.Text.Json;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 压缩收益的历史反馈（v0.25.0 引入，v0.26.0 改为**延迟回弹测量**）。
///
/// v0.25.0 的指标是错的：记录"压后工作集 / 压前工作集"。但 <c>EmptyWorkingSet</c> 几乎总能
/// 把工作集压到接近 0，于是每个进程的"释放率"都是 1.0（2026-11-28 实测 24 条全在 0.989–1.0），
/// 排序退化成"按工作集大小"——和旧逻辑完全一样，等于白做。
///
/// v0.26.0 改用**回弹率**：压完 60 秒后再读一次工作集，
///   effective = 1 - 回弹量 / 立即释放量
/// 活跃进程的页面几十秒内就被读回（effective 低），真正闲置的进程保持低位（effective 高）。
/// 这才是"压它值不值"的真实度量。
///
/// 主键仍是**进程名**：PID 会复用；同名不同实例的回弹特征相近，正是我们要的。
/// </summary>
public sealed class TrimFeedbackStore
{
    /// <summary>没有足够历史时的兜底值。取偏保守：宁可低估也不让排序过度乐观。</summary>
    public const double DefaultRatio = 0.6;
    /// <summary>压完多久再测回弹。太短测不出读回，太长会让样本过时。</summary>
    public const int SettleAfterSeconds = 60;

    private const int MaxEntries = 500;
    private const int MaxPending = 200;
    /// <summary>至少两个样本才用历史值——单次异常不该改变排序。</summary>
    private const int MinSamples = 2;

    private readonly string _path;
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingTrim> _pending = new();
    private readonly object _lock = new();
    private bool _dirty;

    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public int Samples { get; set; }
        /// <summary>平均有效释放率（0..1）：1 = 压完一直没长回来，0 = 立刻回弹。</summary>
        public double AvgRatio { get; set; }
    }

    /// <summary>一次待结算的压缩（60 秒后才读回弹）。</summary>
    public sealed class PendingTrim
    {
        public string Name { get; set; } = "";
        public int Pid { get; set; }
        public DateTime StartTime { get; set; }
        public long WsBefore { get; set; }
        public long WsAfter { get; set; }
        public DateTime AtUtc { get; set; }
    }

    public TrimFeedbackStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "trim-feedback.json");
        // 目录可能还不存在——不建目录会让 Flush 静默失败（v0.25.0 踩过）
        try { Directory.CreateDirectory(dataDir); } catch { }
        Load();
    }

    /// <summary>该进程名的历史有效释放率；样本不足返回 <see cref="DefaultRatio"/>。</summary>
    public double RatioOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return DefaultRatio;
        lock (_lock)
            return _byName.TryGetValue(name, out var e) && e.Samples >= MinSamples
                ? e.AvgRatio
                : DefaultRatio;
    }

    /// <summary>
    /// 记录一次压缩，等 60 秒后再结算回弹（v0.26.0）。wsBefore/wsAfter 都是私有工作集实测值。
    /// </summary>
    public void BeginObservation(string name, int pid, DateTime startTime,
        long wsBefore, long wsAfter, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(name) || wsBefore <= 0 || wsAfter < 0 || wsAfter >= wsBefore) return;
        lock (_lock)
        {
            if (_pending.Count >= MaxPending) _pending.RemoveAt(0);   // 丢最旧的，保住最近的
            _pending.Add(new PendingTrim
            {
                Name = name, Pid = pid, StartTime = startTime,
                WsBefore = wsBefore, WsAfter = wsAfter, AtUtc = nowUtc
            });
        }
    }

    /// <summary>
    /// 结算到期的 pending（v0.26.0）。由引擎的 tick 驱动。
    /// 进程已退出或 PID 被复用（StartTime 不匹配）时直接丢弃——那种样本没有意义。
    /// 返回本次结算的条数（便于日志与测试）。
    /// </summary>
    public int SettleDue(IReadOnlyList<ProcessInfo> snapshot, DateTime nowUtc, Func<int, long> readWorkingSet)
    {
        List<PendingTrim>? due = null;
        lock (_lock)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if ((nowUtc - _pending[i].AtUtc).TotalSeconds < SettleAfterSeconds) continue;
                (due ??= new List<PendingTrim>()).Add(_pending[i]);
                _pending.RemoveAt(i);
            }
        }
        if (due == null) return 0;

        int settled = 0;
        foreach (var p in due)
        {
            if (FindLive(snapshot, p) == null) continue;      // 已退出 / PID 复用
            long wsNow;
            try { wsNow = readWorkingSet(p.Pid); } catch { continue; }
            if (wsNow < 0) continue;

            long freed = p.WsBefore - p.WsAfter;
            if (freed <= 0) continue;
            long rebound = Math.Max(0, wsNow - p.WsAfter);
            double effective = Math.Clamp(1 - rebound / (double)freed, 0, 1);
            ObserveEffective(p.Name, effective);
            settled++;
        }
        if (settled > 0) Flush();
        return settled;
    }

    /// <summary>待结算队列长度（诊断/测试用）。</summary>
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>已知的历史条目数。</summary>
    public int Count { get { lock (_lock) return _byName.Count; } }

    public void Flush()
    {
        List<Entry> snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = _byName.Values.OrderByDescending(x => x.Samples).Take(MaxEntries).ToList();
            _dirty = false;
        }
        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* 记账失败不该影响清理 */ }
    }

    private static ProcessInfo? FindLive(IReadOnlyList<ProcessInfo> snapshot, PendingTrim p)
    {
        foreach (var x in snapshot)
            // StartTime 两次枚举可能有毫秒级抖动，用 1 秒容差
            if (x.Pid == p.Pid && Math.Abs((x.StartTime - p.StartTime).TotalSeconds) < 1)
                return x;
        return null;
    }

    private void ObserveEffective(string name, double effective)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var e))
                _byName[name] = e = new Entry { Name = name };
            e.AvgRatio = (e.AvgRatio * e.Samples + Math.Clamp(effective, 0, 1)) / (e.Samples + 1);
            e.Samples++;
            _dirty = true;
            if (_byName.Count > MaxEntries) TrimColdest();
        }
    }

    private void TrimColdest()
    {
        var keep = _byName.Values.OrderByDescending(x => x.Samples).Take(MaxEntries).ToList();
        _byName.Clear();
        foreach (var e in keep) _byName[e.Name] = e;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path));
            if (list == null) return;
            foreach (var e in list)
                if (!string.IsNullOrEmpty(e.Name)) _byName[e.Name] = e;
        }
        catch { /* 损坏就当作没有历史 */ }
    }
}
