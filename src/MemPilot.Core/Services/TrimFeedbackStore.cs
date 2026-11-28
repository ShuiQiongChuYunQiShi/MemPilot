using System.IO;
using System.Text.Json;

namespace MemPilot.Services;

/// <summary>
/// 压缩收益的历史反馈（v0.25.0）。
///
/// 要解决的问题：温和档固定压 20 个、按"私有工作集降序"选——但**工作集大 ≠ 可回收多**。
/// 2026-11-27 同规模（20/89 与 20/83）的两轮分别释放 1493 MB 与 164 MB，差 9 倍：
/// 有些进程的页面是活跃代码，压完立刻被读回，等于白压。
///
/// 做法：每次批量压缩后逐进程测"私有工作集前后差"，按**进程名**累计
/// "释放率 = 实测释放 / 压前工作集"；排序改用 expected = 工作集 × 历史释放率。
///
/// 为什么按进程名而不是 PID：PID 会复用；进程名（wetype_update 之类）才是有意义的历史主体，
/// 同一程序不同实例的释放率相近，正是我们要的。
/// </summary>
public sealed class TrimFeedbackStore
{
    /// <summary>没有足够历史时的兜底释放率。取偏保守值：宁可低估也不让排序过度乐观。</summary>
    public const double DefaultRatio = 0.6;
    private const int MaxEntries = 500;
    /// <summary>至少观测这么多次才用历史值——单次异常（比如进程刚好退出）不该改变排序。</summary>
    private const int MinSamples = 2;

    private readonly string _path;
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _dirty;

    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public int Samples { get; set; }
        /// <summary>平均释放率（0..1）。</summary>
        public double AvgRatio { get; set; }
    }

    public TrimFeedbackStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "trim-feedback.json");
        // 目录可能还不存在（首次运行 / 测试临时目录）——不建目录会让 Flush 静默失败，
        // 表现为"历史永远为空"，而不是报错。
        try { Directory.CreateDirectory(dataDir); } catch { }
        Load();
    }

    /// <summary>该进程名的历史释放率；样本不足返回 <see cref="DefaultRatio"/>。</summary>
    public double RatioOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return DefaultRatio;
        lock (_lock)
            return _byName.TryGetValue(name, out var e) && e.Samples >= MinSamples
                ? e.AvgRatio
                : DefaultRatio;
    }

    /// <summary>记一次实测。beforeBytes = 压前私有工作集，freedBytes = 实测释放。</summary>
    public void Observe(string name, long beforeBytes, long freedBytes)
    {
        if (string.IsNullOrEmpty(name) || beforeBytes <= 0) return;
        double ratio = Math.Clamp(freedBytes / (double)beforeBytes, 0, 1);
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var e))
                _byName[name] = e = new Entry { Name = name };
            // 增量平均：样本越多越稳，不让单次异常主导
            e.AvgRatio = (e.AvgRatio * e.Samples + ratio) / (e.Samples + 1);
            e.Samples++;
            _dirty = true;
            if (_byName.Count > MaxEntries) TrimColdest();
        }
    }

    /// <summary>落盘。清理动作每 10 分钟才一次，直接同步写可接受；
    /// 失败只影响排序精度，绝不影响清理本身。</summary>
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
            var json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = false });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* 记账失败不该影响清理 */ }
    }

    /// <summary>测试/诊断用：当前已知条目数。</summary>
    public int Count { get { lock (_lock) return _byName.Count; } }

    private void TrimColdest()
    {
        // 保留样本最多的 MaxEntries 条（冷门项淘汰）
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
