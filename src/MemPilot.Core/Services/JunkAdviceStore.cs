using System.IO;
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

    private readonly string _path;
    private readonly object _gate = new();
    private List<JunkAdviceEntry> _entries;

    public JunkAdviceStore(string dataDir)
    {
        try { Directory.CreateDirectory(dataDir); } catch { }
        _path = Path.Combine(dataDir, "junk-advice.json");
        _entries = Load();
    }

    private List<JunkAdviceEntry> Load()
    {
        var list = JsonFileStore.Load<List<JunkAdviceEntry>>(_path) ?? new();
        return list.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
    }

    private void SaveLocked()
    {
        // 上限 500：按 LastSuggestedUtc 淘汰最久未提示的
        if (_entries.Count > MaxEntries)
            _entries = _entries.OrderByDescending(e => e.LastSuggestedUtc).Take(MaxEntries).ToList();
        JsonFileStore.Save(_path, _entries, JsonFileStore.Pretty);
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
