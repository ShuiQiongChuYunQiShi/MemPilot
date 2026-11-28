using System.Diagnostics;
using System.IO;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// Registry of processes this tool has suspended, so they can be resumed and are never
/// forgotten (a suspended process nobody remembers is a zombie holding its memory).
/// Persisted to suspended.json and re-validated on startup: a record only survives if a
/// process with the same (pid, startTime) is still running — PID reuse is never mistaken
/// for the original victim. StartTime is stored in UTC everywhere.
/// </summary>
public class SuspendRegistry
{
    private readonly string _path;
    private readonly Dictionary<int, SuspendRecord> _records = new();
    private readonly object _lock = new();

    public SuspendRegistry(string dataDir)
    {
        _path = Path.Combine(dataDir, "suspended.json");
        try { Directory.CreateDirectory(dataDir); } catch { }
        LoadAndValidate();
    }

    private void LoadAndValidate()
    {
        var list = JsonFileStore.Load<List<SuspendRecord>>(_path);
        if (list == null) return;
        int dropped = 0;
        foreach (var rec in list)
        {
            if (IsRecordAlive(rec)) _records[rec.Pid] = rec;
            else dropped++;
        }
        // v0.20.0（P1）：丢弃死记录后必须落盘——旧实现只清理内存，文件里永久残留幽灵记录
        // （实测 11-18 的两条记录挂了 5 天，内存/UI 都看不到，文件永远脏）。
        if (dropped > 0) Save();
    }

    private static bool IsRecordAlive(SuspendRecord rec)
    {
        try
        {
            using var p = Process.GetProcessById(rec.Pid);
            return p.StartTime.ToUniversalTime() == rec.StartTime;
        }
        catch { return false; }
    }

    public void Add(int pid, DateTime startTime, string name, string reason,
        DateTime? resumeAtUtc = null)
    {
        lock (_lock)
        {
            _records[pid] = new SuspendRecord
            {
                Pid = pid,
                StartTime = startTime.ToUniversalTime(),  // normalize: Process.StartTime is local
                Name = name,
                Reason = reason,
                ResumeAtUtc = resumeAtUtc?.ToUniversalTime()
            };
            Save();
        }
    }

    /// <summary>
    /// 取出所有已到期的定时挂起记录并从账本移除（v0.15.0）。
    /// 调用方负责真正的恢复动作——本类不碰进程，只记账。
    /// </summary>
    public List<SuspendRecord> TakeExpired(DateTime nowUtc)
    {
        var due = new List<SuspendRecord>();
        lock (_lock)
        {
            foreach (var (pid, rec) in _records.ToList())
            {
                if (rec.ResumeAtUtc is { } at && at <= nowUtc)
                {
                    due.Add(rec);
                    _records.Remove(pid);
                }
            }
            if (due.Count > 0) Save();
        }
        return due;
    }

    public bool Remove(int pid)
    {
        lock (_lock)
        {
            if (!_records.Remove(pid)) return false;
            Save();
            return true;
        }
    }

    public bool IsSuspended(int pid)
    {
        lock (_lock) return _records.ContainsKey(pid);
    }

    /// <summary>Live records; drops entries whose process died or was restarted (start time differs).</summary>
    public List<SuspendRecord> LiveRecords()
    {
        var list = new List<SuspendRecord>();
        lock (_lock)
        {
            var dead = new List<int>();
            foreach (var (pid, rec) in _records)
            {
                if (IsRecordAlive(rec)) list.Add(rec);
                else dead.Add(pid);
            }
            if (dead.Count > 0)
            {
                foreach (var pid in dead) _records.Remove(pid);
                Save();
            }
        }
        return list;
    }

    public HashSet<int> SuspendedPids()
    {
        return LiveRecords().Select(r => r.Pid).ToHashSet();
    }

    private void Save() => JsonFileStore.Save(_path, _records.Values.ToList());
}
