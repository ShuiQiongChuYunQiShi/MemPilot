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
    { "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray", "avastsvc", "avgsvc", "avgui", "avastui", "bdagent", "kavfs", "ekrn", "egui", "nortonsecurity", "mcafeemtp", "sophoshealth", "hipsdaemon", "wsctrlsvc", "360safe", "360tray", "usysdiag", "wsctrl", "hipstray", "hrraobr", "huorong", "zhudongfangyu" };

    private static readonly string[] SecurityVendors = { "avast", "avg technologies", "kaspersky", "bitdefender", "nortonlifelock", "norton", "mcafee", "eset", "sophos", "trend micro", "huorong", "qihoo 360" };

    private static readonly HashSet<string> AntiCheatNames = new(StringComparer.OrdinalIgnoreCase)
    { "vgc", "vgtray", "easyanticheat", "easyanticheat_eos", "beservice", "battleyesvc", "battleye", "xigncode", "xigncode3", "nprotect", "gameguard", "punkbuster", "pbsvc", "faceitclient", "riotclientservices", "anticheatexpert", "aceantiego", "ace-helper", "sguard64", "sguardtoc", "sguardupdate64" };

    private static readonly string[] AntiCheatPathFragments = { "\\anticheat\\", "\\anticheatexpert\\", "\\easyanticheat\\", "\\battleye\\", "\\valveanticheat\\" };

    private static readonly HashSet<string> GameLauncherNames = new(StringComparer.OrdinalIgnoreCase)
    { "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "battlenet", "battle.net", "agent", "agent.exe", "origin", "eadesktop", "ealauncher", "eabackgroundservice", "ubisoftconnect", "uplay", "goggalaxy", "riotclient", "riotclientservices", "wegame", "wegamelauncher", "xboxapp", "xblgamesave", "gdkservices", "launcher" };

    private static readonly string[] GamePathFragments = { "\\steamapps\\", "\\epic games\\", "\\riot games\\", "\\games\\", "\\battle.net\\", "\\world of warcraft\\", "\\tencent games\\", "\\游戏\\", "\\wegameapps\\" };

    private static readonly HashSet<string> DevToolNames = new(StringComparer.OrdinalIgnoreCase)
    { "devenv", "code", "code - insiders", "rider", "rider64", "idea", "idea64", "clion", "clion64", "pycharm", "pycharm64", "webstorm", "webstorm64", "goland", "goland64", "phpstorm", "phpstorm64", "rubymine", "datagrip", "vscode", "node", "deno", "bun", "dotnet", "java", "javaw", "python", "pythonw", "gcc", "clang", "clang-cl", "msbuild", "cmake", "ninja", "git", "ssh", "sshd", "bash", "wsl", "wslhost", "wslrelay", "postgres", "mysql", "mysqld", "mongod", "redis-server", "sqlservr", "ssms", "windbg", "x64dbg", "nodejs", "pnpm", "npm", "yarn" };

    private static readonly string[] DevPathFragments = { "\\visual studio", "\\jetbrains\\", "\\docker\\", "\\vagrant.d\\", "\\sdk\\" };

    private static readonly HashSet<string> AiToolNames = new(StringComparer.OrdinalIgnoreCase)
    { "comfyui", "invokeai", "ollama", "ollama app", "llama-server", "llama-server.exe", "lmstudio", "lm-studio", "jan", "jan.exe", "textgen", "koboldcpp", "fooocus", "vram", "sd-webui", "swarmui", "whisper", "ffmpeg" };

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
        ("kingsoft office", ProcessCategory.Bloatware),
        ("vivo mobile", ProcessCategory.BackgroundService),
        ("asustek", ProcessCategory.BackgroundService),
        ("array networks", ProcessCategory.BackgroundService),
        ("openai", ProcessCategory.DevTool),
    };

    // 垃圾常驻：推广位/云助手/常驻更新器等无即时用户价值的后台进程。
    // 结束≠卸载：宿主程序下次启动会按需拉起，分类只影响"优先清理候选"的排序。
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    { "wpscenter", "wpscloudsvr", "wpscloudlaunch", "wpsupdate", "wpsnotify", "wpsdeskmatting", "promecefpluginhost",
      "alibabaprotect", "alipaladin", "alipaladin64", "wetype_update", "lghub_updater" };

    private static readonly string[] JunkVendors = { "kingsoft office" };

    // 按需后台：OEM/厂商配套服务，用户偶发使用（vivo 办公套件、Armoury Crate 等）。
    // 归后台服务而非垃圾：清理只结束当前进程，宿主程序/服务重启即恢复，可用性不受损。
    private static readonly HashSet<string> OnDemandServiceNames = new(StringComparer.OrdinalIgnoreCase)
    { "vivoesservice_x64", "vivosyncservice", "asus_framework", "armourycrate.service", "armourycrate",
      "armourycrate.usersessionhelper", "armourysocketserver", "asusfancontrolservice", "asuscertservice",
      "rogliveservice", "acpowernotification", "atkexcomsvc" };

    private static readonly string[] OnDemandPathFragments = { "\\pcsuite\\", "\\armourydevice\\" };

    // 精确名覆盖：必须先于发布者规则，否则腾讯签名（tencent→游戏平台）会把
    // 微信输入法引擎、QQ、微信、WorkBuddy 等 Tencent 系非游戏应用全部误判成游戏启动器。
    private static readonly HashSet<string> ImeNames = new(StringComparer.OrdinalIgnoreCase)
    { "wetype_server", "wetype_renderer", "wetype_service" };

    private static readonly HashSet<string> TencentAppNames = new(StringComparer.OrdinalIgnoreCase)
    { "weixin", "wechat", "wechatappex", "qq", "qqlive", "qqliveservice", "qmbrowser" };

    private static readonly HashSet<string> KnownDevToolNames = new(StringComparer.OrdinalIgnoreCase)
    { "opencode", "opencode-cli", "token monitor", "tokscale", "open design", "codex-windows-sandbox-service", "everything", "vbcscompiler",
      "workbuddy", "promptoptimizer", "winmemorycleaner" };

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

        // exact-name / path overrides that must beat broad publisher rules (see set comments above)
        if (ImeNames.Contains(name))
        { p.Category = ProcessCategory.ThirdPartyApp; p.CategoryReason = "ime_name"; return; }
        if (TencentAppNames.Contains(name))
        { p.Category = ProcessCategory.ThirdPartyApp; p.CategoryReason = "tencent_app_name"; return; }
        if (KnownDevToolNames.Contains(name))
        { p.Category = ProcessCategory.DevTool; p.CategoryReason = "known_dev_name"; return; }
        if (OnDemandServiceNames.Contains(name) || OnDemandPathFragments.Any(f => path.Contains(f)))
        { p.Category = ProcessCategory.BackgroundService; p.CategoryReason = "on_demand_service"; return; }
        if (JunkNames.Contains(name))
        { p.Category = ProcessCategory.Bloatware; p.CategoryReason = "junk_name"; return; }
        if (p.SignatureVerified && JunkVendors.Any(v => p.SignatureSubject.Contains(v, StringComparison.OrdinalIgnoreCase)))
        { p.Category = ProcessCategory.Bloatware; p.CategoryReason = "junk_vendor"; return; }

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
