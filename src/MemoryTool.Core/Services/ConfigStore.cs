using System.IO;
using System.Text.Json;
using MemoryTool.Models;

namespace MemoryTool.Services;

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
    }
    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { Config = new AppConfig(); }
    }
    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true })); } catch { }
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
                Mode = AppMode.Game, Name = "游戏模式",
                KeepPatterns = new() { "steam", "epicgames", "battlenet", "origin", "riot", "anticheat", "easyanticheat", "battleye", "discord", "nvidia", "amd", "audio", "input" },
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
