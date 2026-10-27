using System.IO;
using System.Text.Json;
using MemoryTool.Models;

namespace MemoryTool.Services;

public class AuditLogger
{
    private readonly string _path;
    private readonly List<AuditEntry> _entries = new();
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
            }
        }
        catch { }
    }
    public void Log(AuditEntry e)
    {
        _entries.Add(e);
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
    public List<AuditEntry> Recent(int n = 500) => _entries.OrderByDescending(e => e.TimestampUtc).Take(n).ToList();
    public int Count => _entries.Count;
}
