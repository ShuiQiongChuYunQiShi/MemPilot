using System.IO;
using System.Text.Json;
using MemPilot.Helpers;

namespace MemPilot.Services;

/// <summary>单进程名的建议历史（v0.15.0）。</summary>
public class JunkAdviceEntry
{
    public string Name { get; set; } = "";
    public DateTime LastSuggestedUtc { get; set; }
    public DateTime LastDismissUtc { get; set; }
    public int DismissCount { get; set; }
    public int AcceptCount { get; set; }
}

/// <summary>
/// 垃圾进程建议的"学习账本"（v0.15.0）。
///
/// 设计立场：**只调整提示频率，永不自动加白名单**。
/// 数据依据（11 天复盘）：HabitHint 111 次发射零响应——单向提示没人看。
/// 所以这里引入反馈闭环：用户忽略越多，提示越稀（7→30→90 天）；
/// 用户接受（挂起/结束）后回到 7 天，说明确实烦人但保留后续机会。
/// </summary>
public class JunkAdviceStore
{
    private const int MaxEntries = 500;
    private static readonly TimeSpan[] DismissCooldowns =
    {
        TimeSpan.FromDays(7),    // 忽略 1 次后
        TimeSpan.FromDays(30),   // 忽略 2 次后
        TimeSpan.FromDays(90),   // 忽略 3 次及以上
    };

    /// <summary>
    /// 落盘结构（v0.17.0 条目 + 当日已交付标记；v0.20.0 增加 RetryAfterUtc 重试冷却）。
    /// RetryAfterUtc 解决 v0.19.0 的"空转"：托盘模式每 tick 都重新评估并回滚标记，
    /// 决策日志 87.7% 是「建议 + 跳过」——冷却把重试压到 15 分钟一次。
    /// </summary>
    private sealed class StoreDto
    {
        public string AdviceDayKey { get; set; } = "";
        /// <summary>v0.21.0：长驻未用建议的当日交付标记（与垃圾建议分列，两者互斥交付）。</summary>
        public string LongResidentDayKey { get; set; } = "";
        public DateTime RetryAfterUtc { get; set; }
        public List<JunkAdviceEntry> Entries { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _gate = new();
    private List<JunkAdviceEntry> _entries = new();
    private string _adviceDayKey = "";
    private string _longResidentDayKey = "";
    private DateTime _retryAfterUtc;

    public JunkAdviceStore(string dataDir)
    {
        try { Directory.CreateDirectory(dataDir); } catch { }
        _path = Path.Combine(dataDir, "junk-advice.json");
        LoadAll();
    }

    private void LoadAll()
    {
        // 兼容 v0.15/0.16 的旧格式（裸数组）与 v0.17 的 {AdviceDayKey, Entries}
        try
        {
            var raw = File.Exists(_path) ? File.ReadAllText(_path) : "";
            if (raw.TrimStart().StartsWith("["))
            {
                _entries = JsonSerializer.Deserialize<List<JunkAdviceEntry>>(raw) ?? new();
            }
            else
            {
                var dto = string.IsNullOrWhiteSpace(raw)
                    ? new StoreDto()
                    : JsonSerializer.Deserialize<StoreDto>(raw) ?? new StoreDto();
                _entries = dto.Entries;
                _adviceDayKey = dto.AdviceDayKey;
                _longResidentDayKey = dto.LongResidentDayKey;
                _retryAfterUtc = dto.RetryAfterUtc;
            }
        }
        catch (Exception ex)
        {
            try { Helpers.Diag.Record("junk-advice-load", ex); } catch { }
            _entries = new();
        }
        _entries = _entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
    }

    private void SaveLocked()
    {
        // 上限 500：按 LastSuggestedUtc 淘汰最久未提示的
        if (_entries.Count > MaxEntries)
            _entries = _entries.OrderByDescending(e => e.LastSuggestedUtc).Take(MaxEntries).ToList();
        JsonFileStore.Save(_path, new StoreDto
        {
            AdviceDayKey = _adviceDayKey,
            LongResidentDayKey = _longResidentDayKey,
            RetryAfterUtc = _retryAfterUtc,
            Entries = _entries
        }, JsonFileStore.Pretty);
    }

    /// <summary>当天是否已交付过一次建议（跨重启）：key 形如 yyyyMMdd。null/空=未交付。</summary>
    public string AdviceDayKey
    {
        get { lock (_gate) return _adviceDayKey; }
    }

    /// <summary>标记当天已交付（弹卡或用户拒绝）。</summary>
    public void MarkAdviceDelivered(string dayKey)
    {
        lock (_gate) { _adviceDayKey = dayKey; SaveLocked(); }
    }

    /// <summary>回滚当天标记（托盘模式没交付成功时用，下一次 tick 可重试）。</summary>
    public void ClearAdviceDay()
    {
        lock (_gate) { _adviceDayKey = ""; SaveLocked(); }
    }

    /// <summary>长驻未用建议的当日交付标记（v0.21.0）：key 形如 yyyyMMdd。</summary>
    public string LongResidentDayKey
    {
        get { lock (_gate) return _longResidentDayKey; }
    }

    /// <summary>标记当天已交付长驻未用建议（跨重启不重复）。</summary>
    public void MarkLongResidentDelivered(string dayKey)
    {
        lock (_gate) { _longResidentDayKey = dayKey; SaveLocked(); }
    }

    /// <summary>回滚长驻未用建议的当日标记。</summary>
    public void ClearLongResidentDay()
    {
        lock (_gate) { _longResidentDayKey = ""; SaveLocked(); }
    }

    /// <summary>下次允许评估建议的 UTC 时刻（v0.20.0；default=不限制）。</summary>
    public DateTime RetryAfterUtc
    {
        get { lock (_gate) return _retryAfterUtc; }
    }

    /// <summary>
    /// 交付失败后的冷却（v0.20.0）：清当日标记 + 设重试下限，两者一并落盘（跨重启有效）。
    /// 取代 v0.17.0 的"每 tick 重试"——托盘模式导致 4,387 条 JunkAdvice 噪声日志。
    /// </summary>
    public void DeferDelivery(TimeSpan cooldown)
    {
        lock (_gate)
        {
            _adviceDayKey = "";
            _retryAfterUtc = DateTime.UtcNow + cooldown;
            SaveLocked();
        }
    }

    /// <summary>该进程名现在是否值得再提示一次（学习后的冷却判定）。</summary>
    public bool ShouldSuggest(string name, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e == null || e.LastSuggestedUtc == default) return true;
            return nowUtc - e.LastSuggestedUtc >= CooldownFor(e.DismissCount);
        }
    }

    /// <summary>忽略次数 → 冷却（0 次=7 天，1 次=30 天，2 次+=90 天）。</summary>
    public static TimeSpan CooldownFor(int dismissCount)
        => dismissCount <= 0 ? DismissCooldowns[0]
         : dismissCount == 1 ? DismissCooldowns[1]
         : DismissCooldowns[2];

    /// <summary>记录一次"已建议"（真正弹卡时调用，用于冷却基准）。</summary>
    public void RecordSuggested(string name, DateTime nowUtc)
    {
        lock (_gate)
        {
            var e = GetOrAddLocked(name);
            e.LastSuggestedUtc = nowUtc;
            SaveLocked();
        }
    }

    /// <summary>用户处理了（挂起/结束）：接受计数 +1，冷却回到 7 天。</summary>
    public void RecordAccepted(string name, DateTime nowUtc)
    {
        lock (_gate)
        {
            var e = GetOrAddLocked(name);
            e.AcceptCount++;
            e.DismissCount = 0;          // 接受了就不再升级冷却
            e.LastSuggestedUtc = nowUtc; // 从此刻起算 7 天
            SaveLocked();
        }
    }

    /// <summary>用户忽略：忽略计数 +1（冷却按 7/30/90 天升级）。</summary>
    public void RecordDismissed(string name, DateTime nowUtc)
    {
        lock (_gate)
        {
            var e = GetOrAddLocked(name);
            e.DismissCount++;
            e.LastDismissUtc = nowUtc;
            e.LastSuggestedUtc = nowUtc;
            SaveLocked();
        }
    }

    /// <summary>当前忽略次数（决策日志"已学会"文案用）。</summary>
    public int DismissCountOf(string name)
    {
        lock (_gate)
            return _entries.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.DismissCount ?? 0;
    }

    private JunkAdviceEntry GetOrAddLocked(string name)
    {
        var e = _entries.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (e != null) return e;
        e = new JunkAdviceEntry { Name = name };
        _entries.Add(e);
        return e;
    }
}
