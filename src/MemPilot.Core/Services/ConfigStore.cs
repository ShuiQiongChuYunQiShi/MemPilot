using System.IO;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

public class ConfigStore
{
    private readonly string _path;
    public AppConfig Config { get; private set; } = new();
    public string DataDirectory { get; }
    public ConfigStore(string dataDir)
    {
        DataDirectory = dataDir;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "config.json");
        Load();
        EnsureDefaults();
        MigrateToEntertainmentMode();
    }

    /// <summary>v0.2.0：游戏模式 → 娱乐模式（游戏/直播/影视是同一个场景，判定不需要区分"玩"还是"看"）。
    /// 老配置迁移：改名 + 补播放器/直播平台保护模式（只增不删，用户自定义不受影响）。</summary>
    private void MigrateToEntertainmentMode()
    {
        if (!Config.ModeProfiles.TryGetValue(AppMode.Game, out var game)) return;
        if (game.Name != "游戏模式") return;
        game.Name = "娱乐模式（游戏/直播/影视）";
        foreach (var p in new[] { "bilibili", "douyu", "huya", "iqiyi", "qyplayer", "qqlive", "potplayer", "vlc", "mpv", "wmplayer", "netflix", "youtube" })
            if (!game.KeepPatterns.Contains(p, StringComparer.OrdinalIgnoreCase))
                game.KeepPatterns.Add(p);
        Save();
    }
    private void Load()
    {
        Config = JsonFileStore.Load<AppConfig>(_path) ?? new AppConfig();
        // v0.3.8：API 密钥加密存储；老配置是明文，原样读入，下次 Save 自动升级为密文
        Config.OnlineLookupApiKey = Dpapi.Unprotect(Config.OnlineLookupApiKey);
    }

    public void Save()
    {
        // 只在落盘这一刻加密，内存里始终是明文（UI/请求都用明文），避免到处解密
        var plain = Config.OnlineLookupApiKey;
        try
        {
            Config.OnlineLookupApiKey = Dpapi.Protect(plain);
            JsonFileStore.Save(_path, Config, JsonFileStore.Pretty);
        }
        finally { Config.OnlineLookupApiKey = plain; }
    }
    private void EnsureDefaults()
    {
        if (Config.ModeProfiles.Count == 0)
        {
            Config.ModeProfiles[AppMode.Development] = new ModeProfile
            {
                Mode = AppMode.Development, Name = "开发模式",
                KeepPatterns = new() { "devenv", "code", "rider", "idea", "dotnet", "node", "python", "docker", "postgres", "mysql", "mongod", "redis", "sqlservr", "wsl", "terminal", "cmd", "powershell" },
                CleanPatterns = new() { "steam", "epicgames", "battlenet", "origin", "netflix", "spotify", "discord overlay", "overwolf" },
                Priority = new() { [ProcessCategory.DevTool] = 10, [ProcessCategory.ContainerVm] = 9, [ProcessCategory.AiImageVideo] = 3 },
                AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim, CleanupActionLevel.L2Suspend, CleanupActionLevel.L3GracefulClose },
                AutoSwitchOnForeground = true
            };
            Config.ModeProfiles[AppMode.Game] = new ModeProfile
            {
                Mode = AppMode.Game, Name = "娱乐模式（游戏/直播/影视）",
                KeepPatterns = new() { "steam", "epicgames", "battlenet", "origin", "riot", "anticheat", "easyanticheat", "battleye", "discord", "nvidia", "amd", "audio", "input",
                                       "bilibili", "douyu", "huya", "iqiyi", "qyplayer", "qqlive", "potplayer", "vlc", "mpv", "wmplayer", "netflix", "youtube" },
                CleanPatterns = new() { "devenv", "code", "rider", "dotnet build", "msbuild", "postgres", "mysql", "mongod", "redis", "ollama", "comfyui", "stable", "docker" },
                Priority = new() { [ProcessCategory.Game] = 10, [ProcessCategory.GameLauncher] = 9, [ProcessCategory.AntiCheat] = 10, [ProcessCategory.DevTool] = 2, [ProcessCategory.AiImageVideo] = 1 },
                AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim, CleanupActionLevel.L2Suspend, CleanupActionLevel.L3GracefulClose },
                AutoSwitchOnForeground = true
            };
            Config.ModeProfiles[AppMode.AiGeneration] = new ModeProfile
            {
                Mode = AppMode.AiGeneration, Name = "AI 生图/视频模式",
                KeepPatterns = new() { "comfyui", "automatic1111", "invokeai", "ollama", "lmstudio", "python", "cuda", "nvidia" },
                CleanPatterns = new() { "steam", "epicgames", "chrome", "edge", "discord", "spotify", "dev", "postgres" },
                Priority = new() { [ProcessCategory.AiImageVideo] = 10, [ProcessCategory.DevTool] = 4, [ProcessCategory.Game] = 1 },
                AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim, CleanupActionLevel.L2Suspend, CleanupActionLevel.L3GracefulClose, CleanupActionLevel.L4ForceKill },
                AutoSwitchOnForeground = true
            };
            Config.ModeProfiles[AppMode.Balanced] = new ModeProfile
            {
                Mode = AppMode.Balanced, Name = "平衡模式",
                KeepPatterns = new(), CleanPatterns = new(),
                Priority = new(), AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim },
                AutoSwitchOnForeground = false
            };
            Config.ModeProfiles[AppMode.Resident] = new ModeProfile
            {
                Mode = AppMode.Resident, Name = "驻守模式",
                KeepPatterns = new(), CleanPatterns = new(),
                Priority = new(), AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim },
                AutoSwitchOnForeground = false
            };
            Config.ProtectedNames = new() { "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer" };
            Save();
        }
    }
}
