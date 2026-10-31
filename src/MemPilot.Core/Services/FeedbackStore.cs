using System.IO;
using System.Text.Json;
using MemPilot.Models;

namespace MemPilot.Services;

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
        // 进程级：名称或路径精确匹配。**不再按发布者泛匹配**——一条"腾讯=游戏"曾会误伤全部腾讯应用。
        var processHit = _entries.FirstOrDefault(f =>
            f.Scope != "software" &&
            ((!string.IsNullOrEmpty(name) && f.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrEmpty(path) && f.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase))));
        if (processHit != null) return processHit;

        // 软件级：exe 落在该软件的安装目录下才算命中（配合程序清单使用）
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var f in _entries.Where(x => x.Scope == "software" && !string.IsNullOrEmpty(x.ExecutablePath)))
            {
                try
                {
                    var dir = Path.GetDirectoryName(f.ExecutablePath);
                    if (!string.IsNullOrEmpty(dir) &&
                        path.StartsWith(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return f;
                }
                catch { }
            }
        }
        return null;
    }
    public void UpdateStatus(Guid id, string status)
    {
        var e = _entries.FirstOrDefault(x => x.Id == id);
        if (e != null) { e.Status = status; Save(); }
    }
}
