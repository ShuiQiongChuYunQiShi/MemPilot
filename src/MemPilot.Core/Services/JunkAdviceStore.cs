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

    /// <summary>落盘结构（v0.17.0）：条目 + 当日已交付标记（跨重启不重复建议）。</summary>
    private sealed class StoreDto
    {
        public string AdviceDayKey { get; set; } = "";
        public List<JunkAdviceEntry> Entries { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _gate = new();
    private List<JunkAdviceEntry> _entries = new();
    private string _adviceDayKey = "";

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
        JsonFileStore.Save(_path, new StoreDto { AdviceDayKey = _adviceDayKey, Entries = _entries },
            JsonFileStore.Pretty);
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
