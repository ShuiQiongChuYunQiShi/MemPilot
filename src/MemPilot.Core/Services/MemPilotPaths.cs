using System.IO;

namespace MemPilot.Services;

/// <summary>
/// 数据目录解析（v0.25.0，从 App.ResolveDataDir 提取到 Core）。
///
/// 为什么搬进来：`mempilot lease run` 是命令行模式，不启动 WPF，但需要和 GUI 读同一个
/// 数据目录（token / config / history）。两份实现迟早漂移——而漂移的后果是"读不到令牌"这种
/// 让人查半天的故障。
///
/// 顺序与旧实现完全一致：AppData → LocalAppData → exe 同级 data → %TEMP%。
/// 已存在但不可写的目录会被跳过（比如受限的 AppData）。
/// </summary>
public static class MemPilotPaths
{
    private static string? _resolved;

    /// <summary>解析并缓存数据目录。首次调用会顺带把 Diag 的日志路径指向该目录。</summary>
    public static string ResolveDataDir()
    {
        if (_resolved != null) return _resolved;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemPilot"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemPilot"),
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Path.GetTempPath(), "MemPilot")
        };

        foreach (var c in candidates)
        {
            try
            {
                Directory.CreateDirectory(c);
                var test = Path.Combine(c, ".writetest");
                File.WriteAllText(test, "x");
                File.Delete(test);
                // 落盘失败诊断（JsonFileStore）写这里；只在首次可用目录时确定
                if (Helpers.Diag.LogPath == null)
                    Helpers.Diag.LogPath = Path.Combine(c, "diag.log");
                _resolved = c;
                return c;
            }
            catch { }
        }

        _resolved = candidates[^1];
        return _resolved;
    }
}
