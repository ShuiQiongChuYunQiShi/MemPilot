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
        // v0.6.0：「线程」列改名「线程数」——老配置里存的旧名做一次幂等迁移，否则列显隐静默失效
        for (int i = 0; i < Config.ProcessHiddenColumns.Count; i++)
            if (Config.ProcessHiddenColumns[i] == "线程") Config.ProcessHiddenColumns[i] = "线程数";
        MigratePersistedKeys();
    }

    /// <summary>
    /// v0.10.0（开源前）：把持久化里的**中文显示文本 key** 换成稳定 id。
    ///
    /// 为什么必须做：`ProcessHiddenColumns` 存的是中文表头，`WorkloadFilter` 存的是中文用途名。
    /// 这两个键一旦跟随界面语言变化，用户调好的列设置与筛选会被静默重置——加英文界面时会
    /// 100% 踩到。迁移是幂等的：已经是 id 的原样通过，识别不出来的值**保留不丢**。
    /// </summary>
    private void MigratePersistedKeys()
    {
        bool changed = false;

        var (cols, colChanged) = PersistedIds.MigrateColumns(Config.ProcessHiddenColumns);
        if (colChanged)
        {
            Config.ProcessHiddenColumns = cols;
            changed = true;
        }

        var (wl, wlChanged) = PersistedIds.MigrateWorkloadFilter(Config.WorkloadFilter);
        if (wlChanged)
        {
            Config.WorkloadFilter = wl;
            changed = true;
        }

        if (changed) Save();   // 幂等：迁移后立刻落盘，避免每次启动重复迁移
    }

    private readonly object _saveLock = new();

    public void Save()
    {
        // v0.9.0：深拷贝后再加密序列化——旧实现把内存里的 key 临时改成密文，
        // 同一时刻引擎线程发 HTTP 会读到 "dpapi:..." 当密钥；ModalProfiles 字典被 UI 并发改也会让序列化抛异常。
        lock (_saveLock)
        {
            AppConfig snapshot;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(Config, JsonFileStore.Compact);
                snapshot = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, JsonFileStore.Compact) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                Helpers.Diag.Record("config:snapshot", ex);
                return;
            }
            snapshot.OnlineLookupApiKey = Dpapi.Protect(snapshot.OnlineLookupApiKey);
            JsonFileStore.Save(_path, snapshot, JsonFileStore.Pretty);
        }
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
            };
            Config.ModeProfiles[AppMode.Game] = new ModeProfile
            {
                Mode = AppMode.Game, Name = "娱乐模式（游戏/直播/影视）",
                KeepPatterns = new() { "steam", "epicgames", "battlenet", "origin", "riot", "anticheat", "easyanticheat", "battleye", "discord", "nvidia", "amd", "audio", "input",
                                       "bilibili", "douyu", "huya", "iqiyi", "qyplayer", "qqlive", "potplayer", "vlc", "mpv", "wmplayer", "netflix", "youtube" },
                CleanPatterns = new() { "devenv", "code", "rider", "dotnet build", "msbuild", "postgres", "mysql", "mongod", "redis", "ollama", "comfyui", "stable", "docker" },
                Priority = new() { [ProcessCategory.Game] = 10, [ProcessCategory.GameLauncher] = 9, [ProcessCategory.AntiCheat] = 10, [ProcessCategory.DevTool] = 2, [ProcessCategory.AiImageVideo] = 1 },
                AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim, CleanupActionLevel.L2Suspend, CleanupActionLevel.L3GracefulClose },
            };
            Config.ModeProfiles[AppMode.AiGeneration] = new ModeProfile
            {
                Mode = AppMode.AiGeneration, Name = "AI 生图/视频模式",
                KeepPatterns = new() { "comfyui", "automatic1111", "invokeai", "ollama", "lmstudio", "python", "cuda", "nvidia" },
                CleanPatterns = new() { "steam", "epicgames", "chrome", "edge", "discord", "spotify", "dev", "postgres" },
                Priority = new() { [ProcessCategory.AiImageVideo] = 10, [ProcessCategory.DevTool] = 4, [ProcessCategory.Game] = 1 },
                AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim, CleanupActionLevel.L2Suspend, CleanupActionLevel.L3GracefulClose, CleanupActionLevel.L4ForceKill },
            };
            Config.ModeProfiles[AppMode.Balanced] = new ModeProfile
            {
                Mode = AppMode.Balanced, Name = "平衡模式",
                KeepPatterns = new(), CleanPatterns = new(),
                Priority = new(), AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim },
            };
            Config.ModeProfiles[AppMode.Resident] = new ModeProfile
            {
                Mode = AppMode.Resident, Name = "驻守模式",
                KeepPatterns = new(), CleanPatterns = new(),
                Priority = new(), AllowedActions = new() { CleanupActionLevel.L0Monitor, CleanupActionLevel.L1Trim },
            };
            Config.ProtectedNames = new() { "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer" };
            Save();
        }
    }
}
