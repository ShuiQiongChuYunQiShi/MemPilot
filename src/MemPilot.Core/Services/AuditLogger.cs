using System.IO;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

public class AuditLogger
{
    private const int MaxEntries = 2000;
    private readonly string _path;
    private readonly List<AuditEntry> _entries = new();
    private readonly object _lock = new();
    public AuditLogger(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "audit.json");
        Load();
    }
    private void Load()
    {
        var list = JsonFileStore.Load<List<AuditEntry>>(_path);
        if (list == null) return;
        _entries.AddRange(list);
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
    }
    public void Log(AuditEntry e)
    {
        lock (_lock)
        {
            _entries.Add(e);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            JsonFileStore.Save(_path, _entries);
        }
    }
    public List<AuditEntry> Recent(int n = 500)
    {
        lock (_lock) return _entries.OrderByDescending(e => e.TimestampUtc).Take(n).ToList();
    }
    public int Count { get { lock (_lock) return _entries.Count; } }
}
