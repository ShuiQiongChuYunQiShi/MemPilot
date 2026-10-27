using System.IO;
using MemoryTool.Models;

namespace MemoryTool.Services;

public class ClassificationEngine
{
    private readonly FeedbackStore _feedback;
    public ClassificationEngine(FeedbackStore feedback) { _feedback = feedback; }

    public void Classify(ProcessInfo p, Dictionary<int, List<ServiceInfo>> servicesByPid, HashSet<string> protectedNames)
    {
        // access denied note
        if (p.AccessDenied && string.IsNullOrEmpty(p.ExecutablePath))
        {
            p.Category = ProcessCategory.Unknown;
            p.CategoryReason = "access_denied_no_path";
            p.OnlineLookupStatus = "not_queried";
            return;
        }

        // feedback override
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

        // system critical detection
        string name = p.Name.ToLowerInvariant();
        string path = p.ExecutablePath.ToLowerInvariant();
        bool isSystemPath = path.StartsWith(@"c:\windows\system32") || path.StartsWith(@"c:\windows\syswow64") || path.StartsWith(@"c:\windows");
        bool isSignedMs = p.IsSigned && (p.SignatureSubject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || p.SignatureIssuer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

        if (servicesByPid.ContainsKey(p.Pid) && servicesByPid[p.Pid].Any())
        {
            var svcs = servicesByPid[p.Pid];
            if (svcs.Any(s => s.IsDriver)) { p.Category = ProcessCategory.Driver; p.IsProtected = true; p.CategoryReason = "service_driver"; return; }
            p.IsService = true;
            p.ServiceName = svcs.First().Name;
        }

        // system critical processes
        var systemCriticalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "taskmgr", "sihost", "fontdrvhost", "ctfmon", "conhost", "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost", "textinputhost", "securityhealthservice", "msmpeng", "mpcmdrun", "sgrmbroker", "spoolsv" };
        if (systemCriticalNames.Contains(p.Name) || name == "system")
        {
            if (name == "system" || name == "registry" || name == "smss" || name == "csrss" || name == "wininit" || name == "winlogon" || name == "lsass")
            {
                p.Category = ProcessCategory.SystemCritical; p.IsSystemProcess = true; p.IsProtected = true; p.CategoryReason = "system_critical_name"; return;
            }
            if (isSystemPath && isSignedMs)
            {
                p.Category = ProcessCategory.SystemService; p.IsSystemProcess = true; p.CategoryReason = "system_service_signed"; 
                if (protectedNames.Contains(p.Name)) p.IsProtected = true;
                return;
            }
        }

        if (isSystemPath && isSignedMs)
        {
            p.Category = ProcessCategory.SystemService;
            p.IsSystemProcess = true;
            p.CategoryReason = "system_path_signed";
            return;
        }

        // drivers via path
        if (path.Contains("\\drivers\\") || path.EndsWith(".sys"))
        {
            p.Category = ProcessCategory.Driver; p.IsProtected = true; p.CategoryReason = "driver_path"; return;
        }

        // security software
        var securityNames = new[] { "msmpeng", "avast", "avg", "kaspersky", "bitdefender", "norton", "mcafee", "eset", "sophos", "defender", "360safe", "huorong" };
        if (securityNames.Any(s => name.Contains(s))) { p.Category = ProcessCategory.SecuritySoftware; p.IsProtected = true; p.CategoryReason = "security_software"; return; }

        // anti-cheat
        var acNames = new[] { "vgc", "vgtray", "easyanticheat", "eac", "battleye", "be", "xigncode", "nprotect", "punkbuster", "vac", "faceit", "esl", "riotclientservices" };
        if (acNames.Any(s => name.Contains(s)) || path.Contains("anticheat") || path.Contains("easyanticheat") || path.Contains("battleye"))
        { p.Category = ProcessCategory.AntiCheat; p.IsProtected = true; p.CategoryReason = "anticheat"; return; }

        // game launchers / games
        var gameLaunchers = new[] { "steam", "epicgameslauncher", "battlenet", "origin", "ea", "ubisoftconnect", "uplay", "gog", "riotclient", "weGame", "tencent", "launcher" };
        var gameEngines = new[] { "unity", "unreal" };
        if (gameLaunchers.Any(s => name.Contains(s))) { p.Category = ProcessCategory.GameLauncher; p.CategoryReason = "game_launcher_name"; return; }

        // game detection by path
        if (path.Contains("\\steam\\") || path.Contains("\\epic games\\") || path.Contains("\\riot games\\") || path.Contains("\\games\\") || path.Contains("steamapps"))
        { p.Category = ProcessCategory.Game; p.CategoryReason = "game_path"; return; }

        // dev tools
        var devNames = new[] { "devenv", "code", "rider", "idea", "clion", "pycharm", "webstorm", "goland", "phpstorm", "rubymine", "datagrip", "vscode", "node", "dotnet", "java", "javaw", "python", "gcc", "g++", "clang", "msbuild", "cmake", "git", "docker", "containerd", "kube", "wsl", "postgres", "mysql", "mongod", "redis", "sqlservr", "ssms" };
        if (devNames.Any(s => name.Contains(s))) { p.Category = ProcessCategory.DevTool; p.CategoryReason = "dev_tool_name"; return; }
        if (path.Contains("\\visual studio") || path.Contains("\\jetbrains\\") || path.Contains("\\docker\\"))
        { p.Category = ProcessCategory.DevTool; p.CategoryReason = "dev_tool_path"; return; }

        // ai image/video
        var aiNames = new[] { "stable", "diffusion", "comfyui", "automatic1111", "a1111", "invokeai", "novelai", "midjourney", "runway", "pika", "sora", "whisper", "ollama", "lmstudio", "textgen", "kobold", "sd", "fooocus" };
        if (aiNames.Any(s => name.Contains(s)) || path.Contains("stable-diffusion") || path.Contains("comfyui") || path.Contains("automatic1111"))
        { p.Category = ProcessCategory.AiImageVideo; p.CategoryReason = "ai_tool_name"; return; }

        // container/vm
        var containerNames = new[] { "docker", "containerd", "vmware", "virtualbox", "vbox", "hyperv", "wsl", "qemu", "parallels" };
        if (containerNames.Any(s => name.Contains(s)) || path.Contains("docker") || path.Contains("vmware"))
        { p.Category = ProcessCategory.ContainerVm; p.CategoryReason = "container_vm"; return; }

        // browser
        var browsers = new[] { "chrome", "firefox", "msedge", "edge", "brave", "opera", "vivaldi", "browser" };
        if (browsers.Any(s => name.Contains(s))) { p.Category = ProcessCategory.ThirdPartyApp; p.CategoryReason = "browser"; return; }

        // third party by path
        if (path.Contains("\\program files\\") || path.Contains("\\program files (x86)\\") || path.Contains("\\users\\"))
        {
            p.Category = ProcessCategory.ThirdPartyApp;
            p.CategoryReason = "third_party_path";
            return;
        }

        // fallback
        if (isSystemPath)
        {
            p.Category = ProcessCategory.SystemService;
            p.IsSystemProcess = true;
            p.CategoryReason = "system_path";
            return;
        }

        p.Category = ProcessCategory.Unknown;
        p.CategoryReason = "unclassified";
    }

    public void ApplyOnlineResult(ProcessInfo p, OnlineLookupResult r)
    {
        p.OnlineLookupStatus = r.Status;
        p.OnlineLookupSummary = r.Summary;
        if (r.SuggestedCategory.HasValue && (p.Category == ProcessCategory.Unknown || p.CategoryReason == "unclassified"))
        {
            p.Category = r.SuggestedCategory.Value;
            p.CategoryReason = "online_lookup";
        }
    }
}
