using System.IO;
using System.Text.Json;

namespace MemPilot.Helpers;

/// <summary>
/// 统一的 JSON 落盘/读取（v0.3.2，棱镜 maintainability/duplication + backend/error-handling）。
///
/// 此前 6 个 Store（DecisionLog / SuspendRegistry / OnlineLookupService / ConfigStore / AuditLogger /
/// FeedbackStore）各自复制「写文件 + 重试 + 空 catch」，任何修复都要改 6 份，失败还完全不可见
/// （K32 事故同类风险：界面正常、数据通路已坏）。这里收敛成一处并接上诊断计数。
///
/// 原子性：先写 .tmp 再 Move(overwrite)，避免写到一半断电留下半截 JSON。
/// </summary>
public static class JsonFileStore
{
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>读取；文件不存在或损坏返回 null（由调用方决定默认值），损坏会记入诊断。</summary>
    public static T? Load<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, options);
        }
        catch (Exception ex)
        {
            StoreDiagnostics.Record("load", Path.GetFileName(path), ex);
            return null;
        }
    }

    /// <summary>原子写入；失败重试 attempts 次，最终失败返回 false 并计入诊断（不再静默）。</summary>
    public static bool Save<T>(string path, T value, JsonSerializerOptions? options = null, int attempts = 3) where T : class
    {
        var tmp = path + ".tmp";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, options ?? Compact));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                // 杀软对刚建目录/文件的瞬时锁很常见（HANDOFF 关键事实 #2）：重试期间不记诊断，
                // 只有最终失败才留证据——否则日志会被误报刷满。
                if (attempt >= attempts)
                {
                    StoreDiagnostics.Record("save", Path.GetFileName(path), ex);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return false;
                }
                System.Threading.Thread.Sleep(100 * attempt);
            }
        }
    }
}

/// <summary>
/// 落盘失败诊断（进程内计数 + 最近 20 条 + 可选追加到 diag.log）。
/// 只记录"最后一次失败"的计数与错误类型，不记录文件内容（避免把进程名单写进日志）。
/// </summary>
public static class StoreDiagnostics
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private static readonly List<string> _recent = new();
    private static DateTime? _lastFailureUtc;

    /// <summary>diag.log 路径；由 App 启动时设置，未设置则只留内存。</summary>
    public static string? LogPath { get; set; }

    public static DateTime? LastFailureUtc { get { lock (_lock) return _lastFailureUtc; } }

    public static int Total
    {
        get { lock (_lock) return _counts.Values.Sum(); }
    }

    public static void Record(string op, string file, Exception ex)
    {
        var key = $"{op}:{file}";
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {key} {ex.GetType().Name}: {ex.Message}";
        lock (_lock)
        {
            _counts[key] = _counts.TryGetValue(key, out var n) ? n + 1 : 1;
            _lastFailureUtc = DateTime.UtcNow;
            _recent.Add(line);
            if (_recent.Count > 20) _recent.RemoveAt(0);
        }
        if (LogPath == null) return;
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    /// <summary>给界面用的一句话；无失败时返回"无"。</summary>
    public static string Summary()
    {
        lock (_lock)
        {
            if (_counts.Count == 0) return "落盘诊断：无失败记录。";
            var top = _counts.OrderByDescending(kv => kv.Value).Take(3)
                             .Select(kv => $"{kv.Key} ×{kv.Value}");
            var when = _lastFailureUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
            return $"落盘诊断：累计失败 {_counts.Values.Sum()} 次（{string.Join("，", top)}），最近 {when}";
        }
    }

    /// <summary>最近失败明细（新→旧），最多 20 条。</summary>
    public static IReadOnlyList<string> Recent()
    {
        lock (_lock) return _recent.AsEnumerable().Reverse().ToList();
    }

    /// <summary>测试用：清空计数。</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _counts.Clear();
            _recent.Clear();
            _lastFailureUtc = null;
        }
    }
}
