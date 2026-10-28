using System.IO;
using System.Text.Json;
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
    private const int MaxEntries = 1000;
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
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<DecisionEntry>>(File.ReadAllText(_path));
            if (list != null) _entries.AddRange(list);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        catch { }
    }

    public void Log(DecisionEntry e)
    {
        lock (_lock)
        {
            _entries.Add(e);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            Save();
        }
    }

    public List<DecisionEntry> Recent(int n = 300)
    {
        lock (_lock) return _entries.OrderByDescending(e => e.TimestampUtc).Take(n).ToList();
    }

    private void Save()
    {
        // AV scanners can transiently lock fresh files; retry briefly, stay quiet otherwise
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = false }));
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
