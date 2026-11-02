using System.IO;
using MemPilot.Helpers;
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
        _entries = JsonFileStore.Load<List<FeedbackEntry>>(_path) ?? new();
        // v0.5.0：清掉"无名称、无路径、无任何标记"的空记录（真机 feedback.json 里有一条，
        // 它永远匹配不到任何进程，只会让人误以为反馈生效了）
        int removed = _entries.RemoveAll(e =>
            string.IsNullOrWhiteSpace(e.ProcessName) &&
            string.IsNullOrWhiteSpace(e.ExecutablePath) &&
            !e.MarkKeep && !e.MarkCleanable && !e.MarkSystem &&
            e.ReportedWorkload == null);
        // 注意：这是数据清洗，不是故障——不要写进 Diag（否则 diag.log 会被"正常清理"污染）
        if (removed > 0) Save();
    }
    public void Save() => JsonFileStore.Save(_path, _entries, JsonFileStore.Pretty);
    public void Add(FeedbackEntry e) { _entries.Add(e); Save(); }
    /// <summary>撤销一条反馈（v0.3.7）：标记错了必须能撤回，否则用户只能重装数据目录。</summary>
    public bool Remove(Guid id)
    {
        var hit = _entries.RemoveAll(e => e.Id == id);
        if (hit > 0) Save();
        return hit > 0;
    }
    public List<FeedbackEntry> All() => _entries.ToList();

    /// <summary>同一目标（名称或路径精确）——分类修正与用途标记共用。</summary>
    private static bool SameTarget(FeedbackEntry f, string name, string path)
        => f.Scope != "software" &&
           ((!string.IsNullOrEmpty(name) && f.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(path) && f.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase)));

    /// <summary>用户标记的用途（v0.4.0）；未标记返回 null，调用方回落到规则判定。</summary>
    public WorkloadKind? FindWorkload(string name, string path)
        => _entries.FirstOrDefault(f => f.ReportedWorkload.HasValue && SameTarget(f, name, path))?.ReportedWorkload;

    /// <summary>设置/清除用途标记：同一目标只保留一条（重复标记是替换，不是叠加）。</summary>
    public void SetWorkload(string name, string path, WorkloadKind? kind)
    {
        _entries.RemoveAll(f => f.ReportedWorkload.HasValue && SameTarget(f, name, path));
        if (kind is { } k)
        {
            _entries.Add(new FeedbackEntry
            {
                ProcessName = name,
                ExecutablePath = path,
                ReportedWorkload = k,
                Scope = "process",
                UserNote = "右键标记用途",
                Status = "applied"
            });
        }
        Save();
    }
    public FeedbackEntry? Find(string name, string path, string publisher)
    {
        // 进程级：名称或路径精确匹配。**不再按发布者泛匹配**——一条"腾讯=游戏"曾会误伤全部腾讯应用。
        var processHit = _entries.FirstOrDefault(f => SameTarget(f, name, path));
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
