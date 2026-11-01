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
            Diag.Record("store:load:" + Path.GetFileName(path), ex);
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
                    Diag.Record("store:save:" + Path.GetFileName(path), ex);
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return false;
                }
                System.Threading.Thread.Sleep(100 * attempt);
            }
        }
    }
}
