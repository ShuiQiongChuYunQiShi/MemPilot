using System.IO;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// Rule-based classifier. Design rules learned from review:
///  - exact process-name matching first; substring matching only for path fragments
///    (bare "ea"/"be"/"vac" substrings matched Teams/Weather/etc. and were removed);
///  - trusted-signature publisher matching is the robust primitive (WinVerifyTrust-backed);
///  - anything unmatched stays Unknown — and Unknown is never auto-acted upon elsewhere.
/// </summary>
public class ClassificationEngine
{
    private readonly FeedbackStore _feedback;
    public ClassificationEngine(FeedbackStore feedback) { _feedback = feedback; }

    private static readonly HashSet<string> SystemCriticalNames = new(StringComparer.OrdinalIgnoreCase)
    { "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "winlogon", "memcompression" };

    private static readonly HashSet<string> SystemShellNames = new(StringComparer.OrdinalIgnoreCase)
    { "explorer", "taskmgr", "sihost", "fontdrvhost", "ctfmon", "conhost", "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost", "textinputhost", "securityhealthservice", "msmpeng", "mpcmdrun", "sgrmbroker", "spoolsv", "audiodg", "searchindexer" };

    private static readonly HashSet<string> SecurityNames = new(StringComparer.OrdinalIgnoreCase)
    { "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray", "avastsvc", "avgsvc", "avgui", "avastui", "bdagent", "kavfs", "ekrn", "egui", "nortonsecurity", "mcafeemtp", "sophoshealth", "hipsdaemon", "wsctrlsvc", "360safe", "360tray", "usysdiag", "wsctrl", "hipstray", "hrraobr", "huorong" };

    private static readonly string[] SecurityVendors = { "avast", "avg technologies", "kaspersky", "bitdefender", "nortonlifelock", "norton", "mcafee", "eset", "sophos", "trend micro", "huorong", "qihoo 360" };

    private static readonly HashSet<string> AntiCheatNames = new(StringComparer.OrdinalIgnoreCase)
    { "vgc", "vgtray", "easyanticheat", "easyanticheat_eos", "beservice", "battleyesvc", "battleye", "xigncode", "xigncode3", "nprotect", "gameguard", "punkbuster", "pbsvc", "faceitclient", "riotclientservices", "anticheatexpert", "aceantiego", "sguard64", "sguardtoc" };

    private static readonly string[] AntiCheatPathFragments = { "\\anticheat\\", "\\easyanticheat\\", "\\battleye\\", "\\valveanticheat\\" };

    private static readonly HashSet<string> GameLauncherNames = new(StringComparer.OrdinalIgnoreCase)
    { "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "battlenet", "battle.net", "agent", "agent.exe", "origin", "eadesktop", "ealauncher", "eabackgroundservice", "ubisoftconnect", "uplay", "goggalaxy", "riotclient", "riotclientservices", "wegame", "wegamelauncher", "xboxapp", "xblgamesave", "gdkservices", "launcher" };

    private static readonly string[] GamePathFragments = { "\\steamapps\\", "\\epic games\\", "\\riot games\\", "\\games\\", "\\battle.net\\", "\\world of warcraft\\", "\\tencent games\\" };

    private static readonly HashSet<string> DevToolNames = new(StringComparer.OrdinalIgnoreCase)
    { "devenv", "code", "code - insiders", "rider", "rider64", "idea", "idea64", "clion", "clion64", "pycharm", "pycharm64", "webstorm", "webstorm64", "goland", "goland64", "phpstorm", "phpstorm64", "rubymine", "datagrip", "vscode", "node", "deno", "bun", "dotnet", "java", "javaw", "python", "pythonw", "gcc", "clang", "clang-cl", "msbuild", "cmake", "ninja", "git", "ssh", "sshd", "bash", "wsl", "wslhost", "wslrelay", "postgres", "mysql", "mysqld", "mongod", "redis-server", "sqlservr", "ssms", "windbg", "x64dbg", "nodejs", "pnpm", "npm", "yarn" };

    private static readonly string[] DevPathFragments = { "\\visual studio", "\\jetbrains\\", "\\docker\\", "\\vagrant.d\\", "\\sdk\\" };

    private static readonly HashSet<string> AiToolNames = new(StringComparer.OrdinalIgnoreCase)
    { "comfyui", "invokeai", "ollama", "ollama app", "lmstudio", "lm-studio", "jan", "jan.exe", "textgen", "koboldcpp", "fooocus", "vram", "sd-webui", "swarmui", "whisper", "ffmpeg" };

    private static readonly string[] AiPathFragments = { "\\stable-diffusion\\", "\\comfyui\\", "\\automatic1111\\", "\\invokeai\\", "\\comfy\\", "\\models\\stable" };

    private static readonly HashSet<string> ContainerVmNames = new(StringComparer.OrdinalIgnoreCase)
    { "docker", "dockerd", "containerd", "docker-desktop", "com.docker.service", "vmware-vmx", "vmware", "virtualbox", "vboxsvc", "vmcompute", "hcsdiag", "qemu-system-x86_64", "qemu", "parallels" };

    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
    { "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera", "vivaldi", "browser_broker", "openconsole" };

    private static readonly (string keyword, ProcessCategory category)[] PublisherRules =
    {
        ("valve", ProcessCategory.GameLauncher),
        ("riot games", ProcessCategory.GameLauncher),
        ("blizzard entertainment", ProcessCategory.GameLauncher),
        ("epic games", ProcessCategory.GameLauncher),
        ("ubisoft", ProcessCategory.GameLauncher),
        ("tencent", ProcessCategory.GameLauncher),
        ("jetbrains", ProcessCategory.DevTool),
        ("unity technologies", ProcessCategory.DevTool),
        ("adobe", ProcessCategory.ThirdPartyApp),
        ("nvidia", ProcessCategory.ThirdPartyApp),
        ("advanced micro devices", ProcessCategory.ThirdPartyApp),
        ("amd", ProcessCategory.ThirdPartyApp),
        ("intel", ProcessCategory.SystemService),
    };

    public void Classify(ProcessInfo p, Dictionary<int, List<ServiceInfo>> servicesByPid, HashSet<string> protectedNames)
    {
        if (p.AccessDenied && string.IsNullOrEmpty(p.ExecutablePath))
        {
            p.Category = ProcessCategory.Unknown;
            p.CategoryReason = "access_denied_no_path";
            p.OnlineLookupStatus = "not_queried";
            return;
        }

        var fb = _feedback.Find(p.Name, p.ExecutablePath, p.SignatureSubject);
        if (fb != null)
        {
            p.Category = fb.ReportedCategory;
            p.CategoryReason = "user_feedback";
            p.IsWhitelisted = fb.MarkKeep;
            p.IsBlacklisted = fb.MarkCleanable;
            if (fb.MarkSystem) { p.IsSystemProcess = true; p.IsProtected = true; }
            return;
        }

        string name = p.Name;
        string path = p.ExecutablePath.ToLowerInvariant();
        bool isSystemPath = path.StartsWith(@"c:\windows\system32") || path.StartsWith(@"c:\windows\syswow64") || path.StartsWith(@"c:\windows");
        bool isMsSigned = p.SignatureVerified &&
            (p.SignatureSubject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

        // attach service info (not a category by itself except for drivers)
        if (servicesByPid.TryGetValue(p.Pid, out var svcs) && svcs.Count > 0)
        {
            if (svcs.Any(s => s.IsDriver))
            {
                p.Category = ProcessCategory.Driver; p.IsProtected = true; p.CategoryReason = "service_driver"; return;
            }
            p.IsService = true;
            p.ServiceName = svcs.First().Name;
        }

        if (SystemCriticalNames.Contains(name) || SystemShellNames.Contains(name))
        {
            if (SystemCriticalNames.Contains(name))
            {
                p.Category = ProcessCategory.SystemCritical; p.IsSystemProcess = true; p.IsProtected = true; p.CategoryReason = "system_critical_name"; return;
            }
            if (isSystemPath && isMsSigned)
            {
                p.Category = ProcessCategory.SystemService; p.IsSystemProcess = true; p.CategoryReason = "system_shell_signed";
                if (protectedNames.Contains(p.Name)) p.IsProtected = true;
                return;
            }
        }

        if (SecurityNames.Contains(name) || SecurityVendors.Any(v => p.SignatureSubject.Contains(v, StringComparison.OrdinalIgnoreCase)))
        {
            p.Category = ProcessCategory.SecuritySoftware; p.IsProtected = true; p.CategoryReason = "security_software"; return;
        }

        if (AntiCheatNames.Contains(name) || AntiCheatPathFragments.Any(f => path.Contains(f)))
        {
            p.Category = ProcessCategory.AntiCheat; p.IsProtected = true; p.CategoryReason = "anticheat"; return;
        }

        if (path.Contains("\\drivers\\") || path.EndsWith(".sys"))
        {
            p.Category = ProcessCategory.Driver; p.IsProtected = true; p.CategoryReason = "driver_path"; return;
        }

        if (isSystemPath && isMsSigned)
        {
            p.Category = ProcessCategory.SystemService; p.IsSystemProcess = true; p.CategoryReason = "system_path_signed"; return;
        }

        if (p.SignatureVerified)
        {
            foreach (var (keyword, category) in PublisherRules)
            {
                if (p.SignatureSubject.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    p.Category = category; p.CategoryReason = $"publisher:{keyword}"; return;
                }
            }
        }

        if (GameLauncherNames.Contains(name))
        {
            p.Category = ProcessCategory.GameLauncher; p.CategoryReason = "launcher_name"; return;
        }

        if (GamePathFragments.Any(f => path.Contains(f)) || GameLibraryService.IsUnderGameLibrary(p.ExecutablePath))
        {
            p.Category = ProcessCategory.Game; p.CategoryReason = "game_path"; return;
        }

        if (DevToolNames.Contains(name) || DevPathFragments.Any(f => path.Contains(f)))
        {
            p.Category = ProcessCategory.DevTool; p.CategoryReason = "dev_tool"; return;
        }

        if (AiToolNames.Contains(name) || AiPathFragments.Any(f => path.Contains(f)))
        {
            p.Category = ProcessCategory.AiImageVideo; p.CategoryReason = "ai_tool"; return;
        }

        if (ContainerVmNames.Contains(name) || path.Contains("\\docker\\") || path.Contains("\\vmware\\"))
        {
            p.Category = ProcessCategory.ContainerVm; p.CategoryReason = "container_vm"; return;
        }

        if (BrowserNames.Contains(name))
        {
            p.Category = ProcessCategory.ThirdPartyApp; p.CategoryReason = "browser"; return;
        }

        if (path.Contains("\\program files\\") || path.Contains("\\program files (x86)\\") || path.Contains("\\users\\"))
        {
            p.Category = ProcessCategory.ThirdPartyApp; p.CategoryReason = "third_party_path"; return;
        }

        if (isSystemPath)
        {
            p.Category = ProcessCategory.SystemService; p.IsSystemProcess = true; p.CategoryReason = "system_path"; return;
        }

        p.Category = ProcessCategory.Unknown;
        p.CategoryReason = "unclassified";
    }

    public void ApplyOnlineResult(ProcessInfo p, OnlineLookupResult r)
    {
        p.OnlineLookupStatus = r.Status;
        p.OnlineLookupSummary = r.Summary;
        if (r.SuggestedCategory.HasValue && p.Category == ProcessCategory.Unknown)
        {
            p.Category = r.SuggestedCategory.Value;
            p.CategoryReason = "online_lookup";
        }
    }
}
