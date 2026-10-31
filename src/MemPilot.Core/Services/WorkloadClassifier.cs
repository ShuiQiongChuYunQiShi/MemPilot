using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 用途分类（v0.3.1）：在进程"身份"（ProcessCategory）之上派生"用途"（娱乐/工作/AI/系统/其他）。
/// 判定顺序：强类别（游戏/AI/系统）→ 名称与清单关键词 → 发布者关键词 → 其余 Unknown/Other。
/// 原则：歧义场景（浏览器、通用三方、聊天工具）不硬猜；用途只用于展示与排序，不构成处置依据。
/// </summary>
public static class WorkloadClassifier
{
    // 娱乐：游戏平台、直播/影视、音乐
    private static readonly string[] EntertainmentNames =
    {
        "steam", "steamwebhelper", "epicgameslauncher", "wegame", "battlenet", "battle.net", "origin", "uplay",
        "ubisoftconnect", "goggalaxy", "riotclient", "riotclientservices", "xboxapp",
        "bilibili", "哔哩哔哩", "douyu", "斗鱼", "huya", "虎牙", "qqlive", "qyplayer", "iqiyi", "爱奇艺",
        "youku", "优酷", "potplayer", "vlc", "mpv", "mpc-hc", "wmplayer", "cloudmusic", "网易云音乐",
        "qqmusic", "spotify", "kugou", "kuwo", "musicbee",
    };

    // 工作：办公/文档/数据库/终端/远程
    private static readonly string[] WorkNames =
    {
        "winword", "excel", "powerpnt", "outlook", "onenote", "msaccess", "kingsoft",
        "notion", "obsidian", "typora", "xmind", "processon", "visio",
        "ssms", "dbeaver", "navicat", "datagrip", "redis-cli", "mongosh", "mysql", "mysqld", "postgres",
        "redis-server", "sqlservr", "mongod",
        "windows terminal", "windowsterminal", "powershell", "mstsc", "wsl",
    };

    // 短名必须精确匹配，禁止子串（v0.3.1 真机教训："et" 把 wetype/SocketServer 误判成工作）
    private static readonly HashSet<string> WorkExactNames = new(StringComparer.OrdinalIgnoreCase)
    { "wps", "et", "wpp", "code", "cmd", "project" };

    private static readonly string[] EntertainmentPublishers =
    {
        "valve", "blizzard", "epic games", "riot games", "tencent games", "netease games",
        "miHoYo", "米哈游", "ubisoft", "electronic arts",
    };

    private static readonly string[] WorkPublishers =
    {
        "jetbrains", "autodesk", "adobe", "kingsoft office", "microsoft office", "siemens", "ansys",
        "unity technologies", "dassault",
    };

    public static void Apply(ProcessInfo p)
    {
        // 1) 强类别：游戏/AI/系统直接定用途
        switch (p.Category)
        {
            case ProcessCategory.Game:
            case ProcessCategory.GameLauncher:
            case ProcessCategory.AntiCheat:
                Set(p, WorkloadKind.Entertainment, "category:game"); return;
            case ProcessCategory.AiImageVideo:
                Set(p, WorkloadKind.Ai, "category:ai"); return;
            case ProcessCategory.SystemCritical:
            case ProcessCategory.SystemService:
            case ProcessCategory.Driver:
            case ProcessCategory.SecuritySoftware:
                Set(p, WorkloadKind.System, "category:system"); return;
            case ProcessCategory.DevTool:
            case ProcessCategory.ContainerVm:
                Set(p, WorkloadKind.Work, "category:dev"); return;
        }

        // 垃圾常驻不参与用途判定（WPS 推广组件不能被当成"工作"）
        if (p.Category == ProcessCategory.Bloatware)
        { Set(p, WorkloadKind.Other, "junk"); return; }

        // 2) 名称 / 清单显示名关键词（短名精确匹配，长名子串）
        var name = p.Name;
        var programName = p.ProgramDisplayName;
        if (MatchesAny(name, EntertainmentNames) || MatchesAny(programName, EntertainmentNames))
        { Set(p, WorkloadKind.Entertainment, "name:entertainment"); return; }
        if (WorkExactNames.Contains(name) || WorkExactNames.Contains(programName))
        { Set(p, WorkloadKind.Work, "name:work_exact"); return; }
        if (MatchesAny(name, WorkNames) || MatchesAny(programName, WorkNames))
        { Set(p, WorkloadKind.Work, "name:work"); return; }

        // 3) 清单发布者关键词（签名不可用时的补充证据）
        var publisher = p.ProgramPublisher;
        if (MatchesAny(publisher, EntertainmentPublishers))
        { Set(p, WorkloadKind.Entertainment, "publisher:entertainment"); return; }
        if (MatchesAny(publisher, WorkPublishers))
        { Set(p, WorkloadKind.Work, "publisher:work"); return; }

        // 4) 其余：明确是"装了的三方程序"记 Other（便于和未识别区分），否则 Unknown
        if (!string.IsNullOrEmpty(programName) || p.Category == ProcessCategory.ThirdPartyApp)
            Set(p, WorkloadKind.Other, "other");
        else
            Set(p, WorkloadKind.Unknown, "unknown");
    }

    private static void Set(ProcessInfo p, WorkloadKind kind, string reason)
    {
        p.Workload = kind;
        p.WorkloadReason = reason;
    }

    private static bool MatchesAny(string? text, string[] words)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var w in words)
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string DisplayName(WorkloadKind kind) => kind switch
    {
        WorkloadKind.Work => "工作",
        WorkloadKind.Entertainment => "娱乐",
        WorkloadKind.Ai => "AI",
        WorkloadKind.System => "系统",
        WorkloadKind.Other => "其他",
        _ => "未知",
    };
}
