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
    /// <summary>程序清单（可选）：只作证据补充，不构成处置依据；构建完成后可由外部替换引用。</summary>
    public ProgramCatalogService? Catalog { get; set; }

    public ClassificationEngine(FeedbackStore feedback, ProgramCatalogService? catalog = null)
    {
        _feedback = feedback;
        Catalog = catalog;
    }

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

    private static readonly string[] DevPathFragments = { "\\visual studio", "\\jetbrains\\", "\\vagrant.d\\", "\\sdk\\" };

    // 容器/虚拟机安装路径：必须在 DevTool 之前判定（2026-10-31 实测 Docker 全家被 \docker\ 路径误标成开发工具）
    private static readonly string[] ContainerVmPathFragments = { "\\docker\\", "\\wsl\\", "\\windowsapps\\docker" };

    private static readonly HashSet<string> AiToolNames = new(StringComparer.OrdinalIgnoreCase)
    { "comfyui", "invokeai", "ollama", "ollama app", "llama-server", "llama-server.exe", "lmstudio", "lm-studio", "jan", "jan.exe", "textgen", "koboldcpp", "fooocus", "vram", "sd-webui", "swarmui", "whisper", "ffmpeg" };

    private static readonly string[] AiPathFragments = { "\\stable-diffusion\\", "\\comfyui\\", "\\automatic1111\\", "\\invokeai\\", "\\comfy\\", "\\models\\stable" };

    private static readonly HashSet<string> ContainerVmNames = new(StringComparer.OrdinalIgnoreCase)
    { "docker", "dockerd", "containerd", "docker-desktop", "docker desktop", "com.docker.service",
      "com.docker.backend", "com.docker.build", "com.docker.dev-envs", "com.docker.proxy", "docker-index", "cagent",
      "wslservice", "wslhost", "wslrelay", "vmmem", "vmmemwsl",
      "vmware-vmx", "vmware", "virtualbox", "vboxsvc", "vmcompute", "hcsdiag", "qemu-system-x86_64", "qemu", "parallels" };

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
        ("docker", ProcessCategory.ContainerVm),
    };

    // 垃圾常驻：推广位/云助手/常驻更新器等无即时用户价值的后台进程。
    // 结束≠卸载：宿主程序下次启动会按需拉起，分类只影响"优先清理候选"的排序。
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    { "wpscenter", "wpscloudsvr", "wpscloudlaunch", "wpsupdate", "wpsnotify", "wpsdeskmatting", "promecefpluginhost",
      "alibabaprotect", "alipaladin", "alipaladin64", "wetype_update", "lghub_updater" };

    /// <summary>垃圾名规则清单（只读，供规则卫生审计 P3.4 使用）。</summary>
    public static IReadOnlyCollection<string> JunkRuleNames => JunkNames;

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

    /// <summary>
    /// 把用户的保留/清理标记写进进程，但**不动** Category / IsProtected / IsSystemProcess。
    /// v0.10.0：硬安全判定命中后调用它——让"我标记过这个服务"仍然可见（反馈页能显示），
    /// 但标记本身不构成处置依据。这正是"安全不变式不可被覆盖"的落点。
    /// </summary>
    private void ApplyFeedbackLabels(ProcessInfo p)
    {
        var fb = _feedback.Find(p.Name, p.ExecutablePath, p.SignatureSubject);
        if (fb == null) return;
        p.IsWhitelisted = fb.MarkKeep;
        p.IsBlacklisted = fb.MarkCleanable;
        // 注意：MarkCleanable 只写 IsBlacklisted（供 UI 显示"用户标记过可清理"），
        // 但 Category 仍是 Driver/AntiCheat/SecuritySoftware...，且 IsProtected=true，
        // 所以 UnusedDetector / JunkSweeper / TrimSelection 都不会选中它。
    }

    public void Classify(ProcessInfo p, Dictionary<int, List<ServiceInfo>> servicesByPid, HashSet<string> protectedNames)
    {
        if (p.AccessDenied && string.IsNullOrEmpty(p.ExecutablePath))
        {
            p.Category = ProcessCategory.Unknown;
            p.CategoryReason = "access_denied_no_path";
            p.OnlineLookupStatus = "not_queried";
            return;
        }

        // ---- 硬安全判定（v0.10.0：**提到用户反馈之前**）----
        // 旧顺序是先查反馈、命中就 return，于是"把某个 Windows 服务标记为可清理"会跳过下面的
        // 服务/驱动检测 → IsService=false → UnusedDetector/JunkSweeper 的 IsService 排除失效。
        // 安全不变式（服务/驱动/反作弊/系统关键/安全软件）**不允许被用户标记覆盖**，
        // 反馈只能覆盖"分类"这一层，不能覆盖"能不能被处置"。
        string name0 = p.Name;
        string path0 = p.ExecutablePath.ToLowerInvariant();
        bool isSystemPath = path0.StartsWith(@"c:\windows\system32")
                            || path0.StartsWith(@"c:\windows\syswow64")
                            || path0.StartsWith(@"c:\windows");
        bool isMsSigned = p.SignatureVerified &&
            (p.SignatureSubject.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

        string? serviceName = null;
        if (servicesByPid.TryGetValue(p.Pid, out var svcs) && svcs.Count > 0)
        {
            if (svcs.Any(s => s.IsDriver))
            {
                p.Category = ProcessCategory.Driver; p.IsProtected = true; p.CategoryReason = "service_driver";
                ApplyFeedbackLabels(p); // 仍记录用户的笨标记，但不改 Category/IsProtected
                return;
            }
            p.IsService = true;
            p.ServiceName = svcs.First().Name;
            serviceName = p.ServiceName;
            _ = serviceName; // 供调试时观察；分类本身不需要这个值
        }

        if (SystemCriticalNames.Contains(name0))
        {
            p.Category = ProcessCategory.SystemCritical; p.IsSystemProcess = true; p.IsProtected = true; p.CategoryReason = "system_critical_name";
            ApplyFeedbackLabels(p);
            return;
        }

        if (SecurityNames.Contains(name0) || SecurityVendors.Any(v => p.SignatureSubject.Contains(v, StringComparison.OrdinalIgnoreCase)))
        {
            p.Category = ProcessCategory.SecuritySoftware; p.IsProtected = true; p.CategoryReason = "security_software";
            ApplyFeedbackLabels(p);
            return;
        }

        if (AntiCheatNames.Contains(name0) || AntiCheatPathFragments.Any(f => path0.Contains(f)))
        {
            p.Category = ProcessCategory.AntiCheat; p.IsProtected = true; p.CategoryReason = "anticheat";
            ApplyFeedbackLabels(p);
            return;
        }

        if (path0.Contains("\\drivers\\") || path0.EndsWith(".sys"))
        {
            p.Category = ProcessCategory.Driver; p.IsProtected = true; p.CategoryReason = "driver_path";
            ApplyFeedbackLabels(p);
            return;
        }

        // ---- 用户反馈/程序清单 ----
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

        var catalogHit = Catalog?.FindByPath(p.ExecutablePath);
        if (catalogHit != null)
        {
            p.ProgramDisplayName = catalogHit.DisplayName;
            p.ProgramPublisher = catalogHit.Publisher;
        }

        string name = name0;
        string path = path0;

        if (SystemShellNames.Contains(name))
        {
            if (isSystemPath && isMsSigned)
            {
                p.Category = ProcessCategory.SystemService; p.IsSystemProcess = true; p.CategoryReason = "system_shell_signed";
                if (protectedNames.Contains(p.Name)) p.IsProtected = true;
                return;
            }
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

        // 程序清单的发布者证据：签名不可用/未验证时的补充（如 Docker Inc 的未签名辅助组件）
        if (catalogHit != null && !string.IsNullOrWhiteSpace(catalogHit.Publisher))
        {
            foreach (var (keyword, category) in PublisherRules)
            {
                if (catalogHit.Publisher.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    p.Category = category; p.CategoryReason = $"program_catalog:{keyword}"; return;
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

        // 容器/虚拟机先于开发工具：Docker/WSL 安装路径含 \docker\、\wsl\，
        // 若 DevTool 先判，整族会被误标"开发工具"且 ContainerVm 分支永远不可达（2026-10-31 实测）
        if (ContainerVmNames.Contains(name) || name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase) ||
            ContainerVmPathFragments.Any(f => path.Contains(f)) || path.Contains("\\vmware\\"))
        {
            p.Category = ProcessCategory.ContainerVm; p.CategoryReason = "container_vm"; return;
        }

        if (DevToolNames.Contains(name) || DevPathFragments.Any(f => path.Contains(f)))
        {
            p.Category = ProcessCategory.DevTool; p.CategoryReason = "dev_tool"; return;
        }

        if (AiToolNames.Contains(name) || AiPathFragments.Any(f => path.Contains(f)))
        {
            p.Category = ProcessCategory.AiImageVideo; p.CategoryReason = "ai_tool"; return;
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

        if (catalogHit != null)
        {
            // 属于某个已安装程序但规则未覆盖：至少不再标"未识别"（证据仅说明"它是个装过的程序"）
            p.Category = ProcessCategory.ThirdPartyApp;
            p.CategoryReason = "program_catalog:" + catalogHit.DisplayName;
            return;
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
