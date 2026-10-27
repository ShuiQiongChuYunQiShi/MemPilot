using System.IO;
using System.Text.Json;
using MemoryTool.Models;

namespace MemoryTool.Services;

public class FeedbackStore
{
    private readonly string _path;
    private List<FeedbackEntry> _entries = new();
    public FeedbackStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "feedback.json");
        Load();
    }
    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _entries = JsonSerializer.Deserialize<List<FeedbackEntry>>(json) ?? new();
            }
        }
        catch { _entries = new(); }
    }
    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
    public void Add(FeedbackEntry e) { _entries.Add(e); Save(); }
    public List<FeedbackEntry> All() => _entries.ToList();
    public FeedbackEntry? Find(string name, string path, string publisher)
    {
        return _entries.FirstOrDefault(f =>
            (f.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(name)) ||
            (!string.IsNullOrEmpty(path) && f.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(publisher) && f.Publisher.Equals(publisher, StringComparison.OrdinalIgnoreCase)));
    }
    public void UpdateStatus(Guid id, string status)
    {
        var e = _entries.FirstOrDefault(x => x.Id == id);
        if (e != null) { e.Status = status; Save(); }
    }
}
