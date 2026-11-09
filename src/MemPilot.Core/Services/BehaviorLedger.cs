using System.Text.Json;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>按进程名累计的行为证据（跨 PID）。只记录、不定级。</summary>
public class ProcessBehavior
{
    public string Name { get; set; } = "";
    public int Observations { get; set; }
    public int NoWindowObs { get; set; }
    public int IdleObs { get; set; }
    public int AudioObs { get; set; }
    public int OrphanObs { get; set; }
    public long PeakPrivateWsBytes { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    // ---- v0.15.0（垃圾进程建议）：今日活跃信号 ----
    /// <summary>最后一次"用户可见活动"（有窗口 / 有音频 / CPU≥2%）的 UTC 时刻。
    /// 与 LastSeenUtc 的区别：在场 ≠ 被使用。JunkAdvisor 用它判定"当天未使用"。</summary>
    public DateTime LastActiveUtc { get; set; }
    /// <summary>今日活跃计数所归属的本地日期（yyyyMMdd）。跨天在 Observe 里自动重置。</summary>
    public string DayKey { get; set; } = "";
    /// <summary>今日被观察到"活跃"的次数（跨天清零）。</summary>
    public int TodayActiveObs { get; set; }
    /// <summary>
    /// 瞬态窗口次数（v0.15.1）：窗口出现 ≤5 分钟后消失、期间无音频、CPU&lt;2%——
    /// 典型的营销弹窗模式。**不计入 LastActiveUtc**（弹窗骚扰 ≠ 用户使用）。
    /// 实测案例：MmMessagingNotifier 弹窗有可见窗口，旧判定把它当成"今天用过"，
    /// 垃圾建议因此漏报。
    /// </summary>
    public int TransientWindowObs { get; set; }

    /// <summary>指定时刻是否已"今日活跃过"（LastActiveUtc 落在该本地日之内）。</summary>
    public bool WasActiveOn(DateTime localDayStartUtc) => LastActiveUtc >= localDayStartUtc;
}

/// <summary>
/// 行为台账：每个刷新周期按进程名累计 在场/无窗口/空闲/有声/孤儿 次数与峰值内存。
/// P3 证据体系的底座——只入画像，永不参与自动定级；"驻留分"的用途是
/// 候选排序与解释（为什么排序靠前），分类门控仍由规则与硬安全不变式决定。
/// </summary>
public class BehaviorLedger
{
    private readonly string _path;
    private int _cycles;
    private int _dirty;

    public Dictionary<string, ProcessBehavior> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    private BehaviorLedger(string path) { _path = path; }

    public static BehaviorLedger Load(string path)
    {
        var ledger = new BehaviorLedger(path);
        try
        {
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize<LedgerDto>(File.ReadAllText(path));
                if (dto != null)
                {
                    ledger._cycles = dto.Cycles;
                    foreach (var e in dto.Entries) ledger.Entries[e.Name] = e;
                }
            }
        }
        catch { }
        return ledger;
    }

    /// <summary>每个刷新周期调用一次（ResidentEngine.AfterRefresh）。约每 60 轮（10s tick ≈ 5 分钟）落盘一次。</summary>
    public void Observe(List<ProcessInfo> procs, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        _cycles++;
        var runningNames = new HashSet<string>(procs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            if (!Entries.TryGetValue(p.Name, out var b))
            {
                b = new ProcessBehavior { Name = p.Name, FirstSeenUtc = now };
                Entries[p.Name] = b;
            }
            b.Observations++;
            if (!p.HasVisibleWindow) b.NoWindowObs++;
            if (p.IdleMinutes >= 1) b.IdleObs++;
            if (p.AudioActive) b.AudioObs++;
            if (p.Memory.PrivateWorkingSetBytes > b.PeakPrivateWsBytes)
                b.PeakPrivateWsBytes = p.Memory.PrivateWorkingSetBytes;
            if (ResidualDetector.FindOrphanHost(p.Name, runningNames) != null) b.OrphanObs++;
            b.LastSeenUtc = now;

            // v0.15.0：今日计数按本地日跨天重置
            string today = now.ToLocalTime().ToString("yyyyMMdd");
            if (b.DayKey != today) { b.DayKey = today; b.TodayActiveObs = 0; }

            // v0.15.1：活跃判定拆成两路。
            // 音频/CPU≥2% → 立即算使用；可见窗口 → 延迟判定（窗口消失时看：
            // ≤5 分钟且无音频且 CPU<2% = 弹窗骚扰，只记 TransientWindowObs；
            // 否则 = 真实使用，回填 LastActiveUtc）。解决"MmMessagingNotifier
            // 弹窗有窗口就被当成今天用过"的漏报。
            bool working = p.AudioActive || p.Memory.CpuPercent >= 2;
            if (working)
            {
                b.LastActiveUtc = now;
                b.TodayActiveObs++;
                _windowStreaks.Remove(p.Name);
            }
            else
            {
                UpdateWindowStreak(p, now, runningNames);
            }
        }

        // 进程已退出但窗口 streak 还挂着：补判定（否则弹完就退出的进程永远停在"进行中"）
        foreach (var gone in _windowStreaks.Keys.Where(k => !runningNames.Contains(k)).ToList())
        {
            if (_windowStreaks.TryGetValue(gone, out var s))
            {
                _windowStreaks.Remove(gone);
                EvaluateEndedStreak(gone, s.StartUtc, s.HadAudio, s.MaxCpu, now);
            }
        }
        // v0.14：上限 1,000 条——超出按 LastSeenUtc 淘汰最久未观察的
        // （旧实现无上限，实测 9 天 368 条且持续 +41/天；每个条目含峰值等字段，长期驻留无意义）
        if (Entries.Count > 1000)
        {
            foreach (var victim in Entries.Values.OrderBy(b => b.LastSeenUtc).Take(Entries.Count - 1000).ToList())
                Entries.Remove(victim.Name);
        }
        if (++_dirty >= 60) { Save(); _dirty = 0; }
    }

    // ---- v0.15.1：窗口生命周期追踪（运行时状态，不持久化）----
    private static readonly TimeSpan TransientWindowMax = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (DateTime StartUtc, int Ticks, bool HadAudio, double MaxCpu)>
        _windowStreaks = new(StringComparer.OrdinalIgnoreCase);

    private void UpdateWindowStreak(ProcessInfo p, DateTime now, HashSet<string> runningNames)
    {
        if (p.HasVisibleWindow)
        {
            if (_windowStreaks.TryGetValue(p.Name, out var s))
                _windowStreaks[p.Name] = (s.StartUtc, s.Ticks + 1, s.HadAudio || p.AudioActive,
                                          Math.Max(s.MaxCpu, p.Memory.CpuPercent));
            else
                _windowStreaks[p.Name] = (now, 1, p.AudioActive, p.Memory.CpuPercent);
            return;   // 可见 ≠ 使用——等窗口消失时统一判定
        }
        if (_windowStreaks.TryGetValue(p.Name, out var ended))
        {
            _windowStreaks.Remove(p.Name);
            EvaluateEndedStreak(p.Name, ended.StartUtc, ended.HadAudio, ended.MaxCpu, now);
        }
    }

    private void EvaluateEndedStreak(string name, DateTime startUtc, bool hadAudio, double maxCpu, DateTime now)
    {
        if (!Entries.TryGetValue(name, out var b)) return;
        if (now - startUtc <= TransientWindowMax && !hadAudio && maxCpu < 2)
        {
            b.TransientWindowObs++;   // 弹窗骚扰：不算使用
        }
        else
        {
            b.LastActiveUtc = now;    // 真实使用（窗口存在超过 5 分钟 / 期间有音频 / CPU 忙）
            string today = now.ToLocalTime().ToString("yyyyMMdd");
            if (b.DayKey != today) { b.DayKey = today; b.TodayActiveObs = 0; }
            b.TodayActiveObs++;
        }
    }

    /// <summary>驻留分 0..10＝(无窗口占比×0.4 + 空闲占比×0.3 + 在场频率×0.3) × 体量因子；
    /// 体量因子对数缩放（峰值 ≥2GB 记满权重）——修正 1MB 系统服务与 400MB 更新器同分的缺陷；
    /// 孤儿证据 +1（封顶 10）。仅用于排序与解释，不构成处置依据。</summary>
    public double JunkScore(string name)
    {
        if (!Entries.TryGetValue(name, out var b) || b.Observations == 0) return 0;
        double noWindow = (double)b.NoWindowObs / b.Observations;
        double idle = (double)b.IdleObs / b.Observations;
        double presence = _cycles <= 0 ? 0 : Math.Min(1.0, (double)b.Observations / _cycles);
        double baseScore = 0.4 * noWindow + 0.3 * idle + 0.3 * presence;
        double peakMb = Math.Max(1, b.PeakPrivateWsBytes / 1048576.0);
        double sizeFactor = Math.Min(1.0, Math.Log10(peakMb) / Math.Log10(2048)); // 1MB≈0 → 2GB+=1
        double score = 10 * baseScore * sizeFactor;
        if (b.OrphanObs > 0) score = Math.Min(10, score + 1);
        return Math.Round(score, 1);
    }

    public void Save()
    {
        try
        {
            var dto = new LedgerDto { Cycles = _cycles, Entries = Entries.Values.ToList() };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { }
    }

    private class LedgerDto
    {
        public int Cycles { get; set; }
        public List<ProcessBehavior> Entries { get; set; } = new();
    }
}
