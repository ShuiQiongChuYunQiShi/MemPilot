using System.IO;
using System.Text.Json;
using MemPilot.Helpers;

namespace MemPilot.Services;

/// <summary>
/// 用途判定名单（v0.4.0，棱镜 product/classification-quality）。
///
/// 为什么外置：名单原先硬编码在 WorkloadClassifier 里约 60 条，误判（如 v0.3.1 的 "et" 子串）
/// 必须改代码发版才能修。现在放进 `%APPDATA%\MemPilot\workload-rules.json`：
///   - 首次启动自动生成一份带注释的默认文件，用户可自行增删；
///   - 文件里出现的**非空数组覆盖**对应内置列表；缺失/空文件则用内置默认（不会因为手滑把名单清空）。
/// </summary>
public class WorkloadRules
{
    public List<string> EntertainmentNames { get; set; } = new();
    public List<string> WorkNames { get; set; } = new();
    public List<string> WorkExactNames { get; set; } = new();
    public List<string> EntertainmentPublishers { get; set; } = new();
    public List<string> WorkPublishers { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>内置默认名单（与旧硬编码完全一致，保证行为不回归）。</summary>
    public static WorkloadRules Defaults() => new()
    {
        EntertainmentNames = new()
        {
            "steam", "steamwebhelper", "epicgameslauncher", "wegame", "battlenet", "battle.net", "origin", "uplay",
            "ubisoftconnect", "goggalaxy", "riotclient", "riotclientservices", "xboxapp",
            "bilibili", "哔哩哔哩", "douyu", "斗鱼", "huya", "虎牙", "qqlive", "qyplayer", "iqiyi", "爱奇艺",
            "youku", "优酷", "potplayer", "vlc", "mpv", "mpc-hc", "wmplayer", "cloudmusic", "网易云音乐",
            "qqmusic", "spotify", "kugou", "kuwo", "musicbee",
        },
        WorkNames = new()
        {
            "winword", "excel", "powerpnt", "outlook", "onenote", "msaccess", "kingsoft",
            "notion", "obsidian", "typora", "xmind", "processon", "visio",
            "ssms", "dbeaver", "navicat", "datagrip", "redis-cli", "mongosh", "mysql", "mysqld", "postgres",
            "redis-server", "sqlservr", "mongod",
            "windows terminal", "windowsterminal", "powershell", "mstsc", "wsl",
        },
        // 短名必须精确匹配，禁止子串（v0.3.1 真机教训："et" 把 wetype/SocketServer 误判成工作）
        WorkExactNames = new() { "wps", "et", "wpp", "code", "cmd", "project" },
        EntertainmentPublishers = new()
        {
            "valve", "blizzard", "epic games", "riot games", "tencent games", "netease games",
            "miHoYo", "米哈游", "ubisoft", "electronic arts",
        },
        WorkPublishers = new()
        {
            "jetbrains", "autodesk", "adobe", "kingsoft office", "microsoft office", "siemens", "ansys",
            "unity technologies", "dassault",
        }
    };

    public static string PathFor(string dataDir) => Path.Combine(dataDir, "workload-rules.json");

    /// <summary>读取用户名单；文件不存在时写一份默认文件（方便用户照着改）。读取失败回落默认。</summary>
    public static WorkloadRules LoadOrCreate(string dataDir)
    {
        var path = PathFor(dataDir);
        try
        {
            if (!File.Exists(path))
            {
                var defaults = Defaults();
                JsonFileStore.Save(path, defaults, Json);
                return defaults;
            }
            var parsed = JsonSerializer.Deserialize<WorkloadRules>(File.ReadAllText(path), Json);
            return Merge(parsed);
        }
        catch (Exception ex)
        {
            Diag.Record("workload-rules", ex);
            return Defaults();
        }
    }

    /// <summary>解析后的合并：非空数组覆盖默认，空/缺失保留默认（防止手滑清空名单）。</summary>
    public static WorkloadRules Merge(WorkloadRules? parsed)
    {
        var d = Defaults();
        if (parsed == null) return d;
        if (parsed.EntertainmentNames.Count > 0) d.EntertainmentNames = parsed.EntertainmentNames;
        if (parsed.WorkNames.Count > 0) d.WorkNames = parsed.WorkNames;
        if (parsed.WorkExactNames.Count > 0) d.WorkExactNames = parsed.WorkExactNames;
        if (parsed.EntertainmentPublishers.Count > 0) d.EntertainmentPublishers = parsed.EntertainmentPublishers;
        if (parsed.WorkPublishers.Count > 0) d.WorkPublishers = parsed.WorkPublishers;
        return d;
    }
}
