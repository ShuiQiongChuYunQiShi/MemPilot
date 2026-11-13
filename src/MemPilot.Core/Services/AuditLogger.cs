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
    private readonly ArchiveWriter _archive;

    public AuditLogger(string dataDir, int archiveRetentionMonths = 12)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "audit.json");
        // v0.18.0：热窗口按条数淘汰，淘汰的条目按天/月归档，不再凭空消失
        _archive = new ArchiveWriter(dataDir, archiveRetentionMonths);
        Load();
    }
    private void Load()
    {
        var list = JsonFileStore.Load<List<AuditEntry>>(_path);
        if (list == null) return;
        _entries.AddRange(list);
        if (_entries.Count > MaxEntries)
        {
            int n = _entries.Count - MaxEntries;
            _archive.Enqueue("audit", _entries.Take(n));
            _entries.RemoveRange(0, n);
        }
    }
    private int _dirty;
    private int _flushScheduled;

    public void Log(AuditEntry e)
    {
        List<AuditEntry>? evicted = null;
        lock (_lock)
        {
            _entries.Add(e);
            if (_entries.Count > MaxEntries)
            {
                int n = _entries.Count - MaxEntries;
                evicted = _entries.Take(n).ToList();
                _entries.RemoveRange(0, n);
            }
            _dirty = 1;
        }
        if (evicted != null) _archive.Enqueue("audit", evicted);   // 锁外入队
        // v0.9.1（复盘 P1）：不再每条都在锁内全量重写文件（腾挪 20 项 = 20 次全量写盘，
        // 且 UI 线程 Recent() 抢同一把锁）。改为合并写：1.5 秒窗口内的多条合并成一次落盘。
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                List<AuditEntry> snapshot;
                lock (_lock)
                {
                    if (_dirty == 0) return;
                    _dirty = 0;
                    snapshot = _entries.ToList();   // 锁内只拷贝，不在锁内做 IO
                }
                JsonFileStore.Save(_path, snapshot);
            }
            catch (Exception ex) { Helpers.Diag.Record("audit:flush", ex); }
            finally { Interlocked.Exchange(ref _flushScheduled, 0); }
        });
    }

    /// <summary>立即落盘（退出前调用，避免最后 1.5 秒的记录丢失）。</summary>
    public void Flush()
    {
        List<AuditEntry> snapshot;
        lock (_lock)
        {
            if (_dirty == 0) { _archive.Flush(); return; }
            _dirty = 0;
            snapshot = _entries.ToList();
        }
        JsonFileStore.Save(_path, snapshot);
        _archive.Flush();
    }
    public List<AuditEntry> Recent(int n = 500)
    {
        lock (_lock) return _entries.OrderByDescending(e => e.TimestampUtc).Take(n).ToList();
    }
    public int Count { get { lock (_lock) return _entries.Count; } }
}
