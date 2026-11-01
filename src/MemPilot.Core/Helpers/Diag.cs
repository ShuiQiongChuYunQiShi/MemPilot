using System.IO;

namespace MemPilot.Helpers;

/// <summary>
/// 统一失败诊断（v0.3.9，棱镜 backend/error-handling）。
///
/// 为什么需要：本项目的空 catch 长期在 80+ 处，其中最危险的不是持久化（已有诊断），
/// 而是**采集类**——进程枚举、程序清单构建、显存采样、场景评估。它们失败不会崩，
/// 只会让界面上的数字悄悄变错（先例：K32 指错 DLL 导致私有工作集全零跑了一个月）。
///
/// 约定：调用方仍保留自己的容错行为（返回空/降级），只是把「发生了什么」记下来。
/// 记录有上限（计数按区域 + 最近 20 条），不会刷爆内存；可选追加到 diag.log。
/// </summary>
public static class Diag
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private static readonly List<string> _recent = new();
    private static DateTime? _lastFailureUtc;

    /// <summary>diag.log 路径；由 App 启动时设置，未设置则只留内存。</summary>
    public static string? LogPath { get; set; }

    public static DateTime? LastFailureUtc { get { lock (_lock) return _lastFailureUtc; } }

    public static int Total { get { lock (_lock) return _counts.Values.Sum(); } }

    /// <summary>记一条异常。area 用「模块[:细节]」形式，例如 store:save:config.json / scene / catalog。</summary>
    public static void Record(string area, Exception ex)
        => Record(area, ex.GetType().Name + ": " + ex.Message);

    /// <summary>记一条无异常的失败（例如"结果为空"）。</summary>
    public static void Record(string area, string detail)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {area} {detail}";
        lock (_lock)
        {
            _counts[area] = _counts.TryGetValue(area, out var n) ? n + 1 : 1;
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
            if (_counts.Count == 0) return "故障诊断：无失败记录。";
            var top = _counts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} ×{kv.Value}");
            var when = _lastFailureUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
            return $"故障诊断：累计 {_counts.Values.Sum()} 次（{string.Join("，", top)}），最近 {when}";
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
