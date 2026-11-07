using System.IO;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 用户反馈存储（保留/可清理/系统/用途标记）。
///
/// v0.13.0（四轮复盘 D1）：**写时复制（copy-on-write）**。
/// 旧实现是一把裸 `List`：UI 线程 Add/Remove 与引擎后台分类线程 Find（每 tick、每进程）
/// 并发访问，枚举中被改会抛 "Collection was modified" 且撕裂读。
/// 现在：读走 `volatile` 快照数组（无锁），写加锁重建快照。反馈条目量级是几十条，
/// 每次写复制一份的开销可以忽略，换来读路径完全无竞争。
/// </summary>
public class FeedbackStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private FeedbackEntry[] _snapshot = Array.Empty<FeedbackEntry>();

    public FeedbackStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "feedback.json");
        Load();
    }

    private void Load()
    {
        var entries = JsonFileStore.Load<List<FeedbackEntry>>(_path) ?? new();
        // v0.5.0：清掉"无名称、无路径、无任何标记"的空记录（真机 feedback.json 里有一条，
        // 它永远匹配不到任何进程，只会让人误以为反馈生效了）
        int removed = entries.RemoveAll(e =>
            string.IsNullOrWhiteSpace(e.ProcessName) &&
            string.IsNullOrWhiteSpace(e.ExecutablePath) &&
            !e.MarkKeep && !e.MarkCleanable && !e.MarkSystem &&
            e.ReportedWorkload == null);
        // 注意：这是数据清洗，不是故障——不要写进 Diag（否则 diag.log 会被"正常清理"污染）
        Publish(entries);
        if (removed > 0) Save();
    }

    /// <summary>把工作列表发布为不可变快照（写路径收口点）。</summary>
    private void Publish(List<FeedbackEntry> entries)
        => System.Threading.Volatile.Write(ref _snapshot, entries.ToArray());

    public void Save()
    {
        lock (_gate) JsonFileStore.Save(_path, _snapshot.ToList(), JsonFileStore.Pretty);
    }

    public void Add(FeedbackEntry e)
    {
        lock (_gate)
        {
            var list = _snapshot.ToList();
            list.Add(e);
            Publish(list);
            JsonFileStore.Save(_path, list, JsonFileStore.Pretty);
        }
    }

    /// <summary>撤销一条反馈（v0.3.7）：标记错了必须能撤回，否则用户只能重装数据目录。</summary>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var list = _snapshot.ToList();
            int hit = list.RemoveAll(e => e.Id == id);
            if (hit > 0)
            {
                Publish(list);
                JsonFileStore.Save(_path, list, JsonFileStore.Pretty);
            }
            return hit > 0;
        }
    }

    /// <summary>全部条目副本（UI 显示用；返回快照直接引用即可——数组内容不被改写）。</summary>
    public List<FeedbackEntry> All() => _snapshot.ToList();

    /// <summary>同一目标（名称或路径精确）——分类修正与用途标记共用。</summary>
    private static bool SameTarget(FeedbackEntry f, string name, string path)
        => f.Scope != "software" &&
           ((!string.IsNullOrEmpty(name) && f.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(path) && f.ExecutablePath.Equals(path, StringComparison.OrdinalIgnoreCase)));

    /// <summary>用户标记的用途（v0.4.0）；未标记返回 null，调用方回落到规则判定。
    /// 引擎后台线程每 tick 调用——读快照，无锁。</summary>
    public WorkloadKind? FindWorkload(string name, string path)
        => _snapshot.FirstOrDefault(f => f.ReportedWorkload.HasValue && SameTarget(f, name, path))?.ReportedWorkload;

    /// <summary>设置/清除用途标记：同一目标只保留一条（重复标记是替换，不是叠加）。</summary>
    public void SetWorkload(string name, string path, WorkloadKind? kind)
    {
        lock (_gate)
        {
            var list = _snapshot.ToList();
            list.RemoveAll(f => f.ReportedWorkload.HasValue && SameTarget(f, name, path));
            if (kind is { } k)
            {
                list.Add(new FeedbackEntry
                {
                    ProcessName = name,
                    ExecutablePath = path,
                    ReportedWorkload = k,
                    Scope = "process",
                    UserNote = "右键标记用途",
                    Status = "applied"
                });
            }
            Publish(list);
            JsonFileStore.Save(_path, list, JsonFileStore.Pretty);
        }
    }

    public FeedbackEntry? Find(string name, string path, string publisher)
    {
        // 进程级：名称或路径精确匹配。**不再按发布者泛匹配**——一条"腾讯=游戏"曾会误伤全部腾讯应用。
        var snap = _snapshot;
        var processHit = snap.FirstOrDefault(f => SameTarget(f, name, path));
        if (processHit != null) return processHit;

        // 软件级：exe 落在该软件的安装目录下才算命中（配合程序清单使用）
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var f in snap.Where(x => x.Scope == "software" && !string.IsNullOrEmpty(x.ExecutablePath)))
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
        lock (_gate)
        {
            var list = _snapshot.ToList();
            var e = list.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            e.Status = status;
            Publish(list);
            JsonFileStore.Save(_path, list, JsonFileStore.Pretty);
        }
    }
}
