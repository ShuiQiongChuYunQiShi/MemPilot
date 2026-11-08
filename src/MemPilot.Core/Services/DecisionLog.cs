using System.IO;
using System.Text.Json;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// The engine's decision log: why it switched modes, which auto-cleanups it executed,
/// and why it skipped others. Automatic behavior must be explainable — this is the
/// place to look when "it did something / didn't do something" and you want to know why.
/// Capped at 1000 entries, persisted to decisions.json.
/// </summary>
public class DecisionLog
{
    // v0.9.0：1000 条时已用 82%（818/1000），两天内会丢最早记录；抬到 5000（约 1.1MB）
    private const int MaxEntries = 5000;

    /// <summary>
    /// 重要事件配额（v0.9.2，三轮复盘 P3）：Lease / AiSession / CommitRelief / ModeSwitch*
    /// 这些是"复盘时真正要看"的记录，但数量极少（实测 9 天里 Lease 8 条、AiSession 6 条）。
    /// 如果和 GrowthWatch（290 条）/ AutoOptimize（334 条）混在一个 FIFO 里，
    /// 噪声会把它们挤出窗口——留 800 个位置只给它们，且永不按类别淘汰。
    /// </summary>
    private static readonly HashSet<string> ImportantKinds = new(StringComparer.Ordinal)
    {
        "Lease", "AiSession", "CommitRelief", "ModeSwitch", "ModeSwitchSuppressed", "ModeSwitchStorm",
        "RuleAudit", "PromoteRule", "Diag"
    };
    private const int ImportantQuota = 800;
    private readonly string _path;
    private readonly List<DecisionEntry> _entries = new();
    private readonly object _lock = new();

    public DecisionLog(string dataDir)
    {
        _path = Path.Combine(dataDir, "decisions.json");
        try { Directory.CreateDirectory(dataDir); } catch { }
        Load();
    }

    private void Load()
    {
        var list = JsonFileStore.Load<List<DecisionEntry>>(_path);
        if (list == null) return;
        _entries.AddRange(list);
        Trim();
    }

    private int _dirty;
    private int _flushScheduled;

    public void Log(DecisionEntry e)
    {
        lock (_lock)
        {
            _entries.Add(e);
            Trim();
            _dirty = 1;
        }
        // v0.9.2：不再每条全量重写（5000 条 × 每次动作 = 明显卡顿，且腾挪时每条都要写一次盘）。
        // 与 AuditLogger 同一策略：1.5 秒窗口合并落盘，退出前 Flush()。
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (System.Threading.Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                List<DecisionEntry> snapshot;
                lock (_lock)
                {
                    if (_dirty == 0) return;
                    _dirty = 0;
                    snapshot = _entries.ToList();
                }
                JsonFileStore.Save(_path, snapshot);
            }
            catch (Exception ex) { Diag.Record("decisions:flush", ex); }
            finally { System.Threading.Interlocked.Exchange(ref _flushScheduled, 0); }
        });
    }

    /// <summary>立即落盘（退出前调用）。</summary>
    public void Flush()
    {
        List<DecisionEntry> snapshot;
        lock (_lock)
        {
            if (_dirty == 0) return;
            _dirty = 0;
            snapshot = _entries.ToList();
        }
        JsonFileStore.Save(_path, snapshot);
    }

    /// <summary>
    /// 淘汰策略（v0.9.2）：先按 FIFO 压到 MaxEntries；若重要事件仍超过配额，
    /// **从最老的次要记录开始删**，而不是连重要记录一起从头部切。
    /// </summary>
    /// <summary>
    /// 淘汰策略（v0.9.2）：**先配额、后总量**，且永远优先删噪声。
    ///
    /// 旧实现先 `RemoveRange(0, 超出量)` 再从头部切——而列表是按时间追加的，
    /// 最早的恰恰是 Lease/AiSession 这类重要记录，于是"保重要"完全失效
    /// （测试当场抓到：10 条 Lease 被 5200 条 GrowthWatch 挤掉）。
    /// </summary>
    private void Trim()
    {
        // ① 重要记录超过配额：从最老的开始删重要记录（同类内 FIFO，保证仍是最新优先）
        var importantIdx = new List<int>();
        for (int i = 0; i < _entries.Count; i++)
            if (ImportantKinds.Contains(_entries[i].Kind)) importantIdx.Add(i);

        if (importantIdx.Count > ImportantQuota)
        {
            int excess = importantIdx.Count - ImportantQuota;
            // 从后往前删，索引不失效（删的是最老的那批）
            for (int k = excess - 1; k >= 0; k--)
                _entries.RemoveAt(importantIdx[k]);
        }

        // ② 总量超限：仍从最老的次要记录删起，尽量不动重要记录
        while (_entries.Count > MaxEntries)
        {
            int dropAt = _entries.FindIndex(e => !ImportantKinds.Contains(e.Kind));
            if (dropAt < 0) break;              // 全是重要记录（不可能：配额已限）
            _entries.RemoveAt(dropAt);
        }

        // ③ 兜底：若噪音删完了还超限（极端情况），才从头部切
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
    }

    /// <summary>各 Kind 的当前条数（诊断页/测试用）。</summary>
    public Dictionary<string, int> CountByKind()
    {
        lock (_lock) return _entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// 最近 n 条（新的在前）。
    /// v0.14 性能债：列表本身按时间追加序（Load/Log 都是尾部 Add，Trim 只做删除保序），
    /// 旧实现在锁内对全量 5,000 条做 OrderByDescending 拷贝——RuleAudit 一次调用 3 遍 Recent
    /// 就是 15,000 条排序。直接取尾部反转，O(n)。
    /// </summary>
    public List<DecisionEntry> Recent(int n = 300)
    {
        lock (_lock)
        {
            if (n >= _entries.Count) return _entries.AsEnumerable().Reverse().ToList();
            var tail = _entries.GetRange(_entries.Count - n, n);
            tail.Reverse();
            return tail;
        }
    }

}

/// <summary>Processes this tool has closed/killed, persisted to killed.json (capped) so the
/// user can relaunch an exe after an L3/L4 — the closest thing to an undo for a kill.</summary>
public class DisposalRegistry
{
    private const int MaxEntries = 50;
    private readonly string _path;
    private readonly List<DisposalRecord> _records = new();
    private readonly object _lock = new();

    public DisposalRegistry(string dataDir)
    {
        _path = Path.Combine(dataDir, "killed.json");
        try { Directory.CreateDirectory(dataDir); } catch { }
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<DisposalRecord>>(File.ReadAllText(_path));
                if (list != null) _records.AddRange(list);
            }
        }
        catch { }
    }

    public void Add(DisposalRecord rec)
    {
        lock (_lock)
        {
            _records.Insert(0, rec);
            if (_records.Count > MaxEntries) _records.RemoveRange(MaxEntries, _records.Count - MaxEntries);
            Save();
        }
    }

    public List<DisposalRecord> All()
    {
        lock (_lock) return _records.ToList();
    }

    private void Save()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = false }));
                return;
            }
            catch (Exception)
            {
                if (attempt >= 3) return;
                System.Threading.Thread.Sleep(100 * attempt);
            }
        }
    }
}
