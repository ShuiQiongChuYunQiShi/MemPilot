using System.IO;
using System.Text.Json;
using MemoryTool.Models;

namespace MemoryTool.Services;

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
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<AuditEntry>>(json);
                if (list != null) _entries.AddRange(list);
                if (_entries.Count > MaxEntries)
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }
        catch { }
    }
    public void Log(AuditEntry e)
    {
        lock (_lock)
        {
            _entries.Add(e);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = false }));
            }
            catch { }
        }
    }
    public List<AuditEntry> Recent(int n = 500)
    {
        lock (_lock) return _entries.OrderByDescending(e => e.TimestampUtc).Take(n).ToList();
    }
    public int Count { get { lock (_lock) return _entries.Count; } }
}
