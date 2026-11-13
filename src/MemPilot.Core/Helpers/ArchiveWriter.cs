using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MemPilot.Helpers;

/// <summary>
/// 审计/决策日志的冷数据归档（v0.18.0）。
///
/// 热窗口（内存里 2,000 / 5,000 条）保证 UI 快速读取；被淘汰的条目不能就这么消失——
/// "每个自动决策都可审计"是产品承诺，而按条数 FIFO 会在两三周后把"谁在什么时候杀了
/// 我的进程"挤掉（实测 GrowthWatch 290 条/天 + AutoOptimize 334 条/天）。这里把淘汰
/// 条目追加到按月归档的 JSONL，默认保留 12 个月。
///
/// 约束：调用方在锁内（Trim）高频触发，因此 <see cref="Enqueue{T}"/> 只做内存入队；
/// 落盘由 1.5 秒合并的后台任务完成，失败只记 Diag，不影响主流程。
/// </summary>
public class ArchiveWriter
{
    private readonly string _dir;
    private readonly ConcurrentQueue<(string path, string line)> _queue = new();
    private int _scheduled;

    public ArchiveWriter(string dataDir, int retentionMonths = 12)
    {
        _dir = Path.Combine(dataDir, "archive");
        try { Directory.CreateDirectory(_dir); } catch { }
        Prune(retentionMonths);
    }

    public string DirectoryPath => _dir;

    /// <summary>把被淘汰的条目加入归档队列（只入内存，不落盘）。</summary>
    public void Enqueue<T>(string prefix, IEnumerable<T> items)
    {
        var path = Path.Combine(_dir, $"{prefix}-{DateTime.Now:yyyyMM}.jsonl");
        bool any = false;
        foreach (var item in items)
        {
            _queue.Enqueue((path, JsonSerializer.Serialize(item)));
            any = true;
        }
        if (any) Schedule();
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                Drain();
            }
            finally { Interlocked.Exchange(ref _scheduled, 0); }
        });
    }

    /// <summary>立即把队列写盘（退出前调用，避免最后 1.5 秒的淘汰记录丢失）。</summary>
    public void Flush() => Drain();

    private void Drain()
    {
        var batch = new Dictionary<string, StringBuilder>();
        while (_queue.TryDequeue(out var item))
        {
            if (!batch.TryGetValue(item.path, out var sb))
                batch[item.path] = sb = new StringBuilder();
            sb.AppendLine(item.line);
        }
        foreach (var (path, sb) in batch)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(path, sb.ToString());
            }
            catch (Exception ex) { Diag.Record("archive:append", ex); }
        }
    }

    /// <summary>删除早于 months 个月的 {prefix}-yyyyMM.jsonl，返回删除数。</summary>
    public int Prune(int months)
    {
        int removed = 0;
        try
        {
            var cutoff = DateTime.Now.AddMonths(-Math.Max(1, months));
            foreach (var file in Directory.GetFiles(_dir, "*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);   // prefix-yyyyMM
                if (name.Length < 7) continue;
                if (DateTime.TryParseExact(name[^6..], "yyyyMM", null,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var month) &&
                    month < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
        }
        catch { }
        return removed;
    }
}
