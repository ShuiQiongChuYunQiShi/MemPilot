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
        }
        if (++_dirty >= 60) { Save(); _dirty = 0; }
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
