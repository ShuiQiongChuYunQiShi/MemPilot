namespace MemPilot.Models;

public enum ProcessCategory
{
    Unknown,
    SystemCritical,
    SystemService,
    Driver,
    ThirdPartyApp,
    Game,
    GameLauncher,
    AntiCheat,
    DevTool,
    AiImageVideo,
    ContainerVm,
    BackgroundService,
    Bloatware,
    SecuritySoftware
}

public enum ProcessRiskLevel { Low, Medium, High, Critical }

public enum CleanupActionLevel
{
    L0Monitor,
    L1Trim,
    L2Suspend,
    L3GracefulClose,
    L4ForceKill
}

public enum AppMode { Resident, Development, Game, AiGeneration, Balanced }

public class MemoryMetrics
{
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }              // commit charge
    public long CommitBytes { get; set; }               // same as PrivateBytes (private commit)
    public long PeakWorkingSetBytes { get; set; }
    public long SharedCommitBytes { get; set; }
    public long PrivateWorkingSetBytes { get; set; }    // physical pages unique to this process
    public long PagedPoolBytes { get; set; }
    public long NonPagedPoolBytes { get; set; }
    public long VirtualBytes { get; set; }
    public long PeakVirtualBytes { get; set; }
    public long IoReadBytes { get; set; }
    public long IoWriteBytes { get; set; }
    public long IoReadOps { get; set; }
    public long IoWriteOps { get; set; }
    public double IoReadRateBytesPerSec { get; set; }
    public double IoWriteRateBytesPerSec { get; set; }
    public int NetworkConnections { get; set; }
    public double CpuPercent { get; set; }
    public TimeSpan TotalCpuTime { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
}

public class ProcessInfo
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string Company { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string FileDescription { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public string SignatureSubject { get; set; } = "";
    public string SignatureIssuer { get; set; } = "";
    public bool IsSigned { get; set; }
    public bool SignatureVerified { get; set; }         // full WinVerifyTrust result
    public bool IsSystemProcess { get; set; }
    public bool IsService { get; set; }
    public bool IsStartupItem { get; set; }
    public bool IsScheduledTask { get; set; }
    public string ServiceName { get; set; } = "";
    public bool HasVisibleWindow { get; set; }
    public string MainWindowTitle { get; set; } = "";
    public DateTime StartTime { get; set; }
    public double IdleMinutes { get; set; }
    public MemoryMetrics Memory { get; set; } = new();
    public ProcessCategory Category { get; set; } = ProcessCategory.Unknown;
    public string CategoryReason { get; set; } = "";
    public ProcessRiskLevel Risk { get; set; } = ProcessRiskLevel.Low;
    public bool IsProtected { get; set; }
    public bool IsWhitelisted { get; set; }
    public bool IsBlacklisted { get; set; }
    public bool AccessDenied { get; set; }
    public string AccessNote { get; set; } = "";
    public string OnlineLookupStatus { get; set; } = "not_queried";
    public string OnlineLookupSummary { get; set; } = "";
    public List<int> ChildPids { get; set; } = new();
    public List<ThreadInfo> Threads { get; set; } = new();
    public bool IsUnusedCandidate { get; set; }
    public long EstimatedReclaimableBytes { get; set; }
    public bool AudioActive { get; set; }
    public bool IsSuspended { get; set; }
    public bool ModeKeep { get; set; }  // current mode's KeepPattern match: excluded from auto-actions
    public long MemoryGrowthBytes { get; set; }  // WS growth over the tracked window (leak watch)
    public int TreeDepth { get; set; }          // tree view: 0 = root (runtime-only)
    public bool HasChildren { get; set; }       // tree view: has in-set children
    public bool Collapsed { get; set; }         // tree view: node is collapsed in the UI

    /// <summary>Stable identity across refreshes: pid + start time defeats PID reuse.</summary>
    public (int pid, DateTime start) StateKey => (Pid, StartTime);
}

public class ThreadInfo
{
    public int Tid { get; set; }
    public int OwnerPid { get; set; }
    public System.Diagnostics.ThreadState State { get; set; }
    public System.Diagnostics.ThreadWaitReason WaitReason { get; set; }
    public int Priority { get; set; }
    public TimeSpan TotalCpuTime { get; set; }
    public DateTime StartTime { get; set; }
    public string Note { get; set; } = "";
}

public class ProcessGroup
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ProcessCategory Category { get; set; }
    public List<ProcessInfo> Processes { get; set; } = new();
    public long TotalWorkingSetBytes => Processes.Sum(p => p.Memory.WorkingSetBytes);
    public long TotalPrivateBytes => Processes.Sum(p => p.Memory.PrivateBytes);
    public long TotalCommitBytes => Processes.Sum(p => p.Memory.CommitBytes);
    public int Count => Processes.Count;
}

public class UnusedCandidate
{
    public ProcessInfo Process { get; set; } = new();
    public List<string> Reasons { get; set; } = new();
    public string ReasonsText => string.Join(", ", Reasons);
    public long EstimatedReclaimableBytes { get; set; }
    public long EstimatedCommitReclaimableBytes { get; set; }
    public ProcessRiskLevel Risk { get; set; }
    public CleanupActionLevel SuggestedLevel { get; set; }
    /// <summary>驻留分 0..10（行为台账）：解释排序用，不构成处置依据。</summary>
    public double JunkScore { get; set; }
}

public class AuditEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Level { get; set; } = "";
    public string Result { get; set; } = "";
    public long MemoryBeforeBytes { get; set; }
    public long MemoryAfterBytes { get; set; }
    public string Details { get; set; } = "";
}

/// <summary>One line of "why did the engine do/skip that" — makes automatic behavior explainable.</summary>
public class DecisionEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "";      // ModeSwitch / AutoCleanup / AutoOptimize / GrowthWatch
    public string Target { get; set; } = "";    // process "name (PID N)" or "-"
    public string Decision { get; set; } = "";  // 执行 / 跳过 / 切换 / 提示
    public string Reason { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    [System.Text.Json.Serialization.JsonIgnore]
    public string LineText => $"{Decision} · {Target} · {Reason}";
}

/// <summary>A process this tool closed or killed — kept so the user can relaunch it (the L4 undo).</summary>
public class DisposalRecord
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Action { get; set; } = "";
    public bool Exited { get; set; }
    public string Display => $"{Name} (PID {Pid})  {Action}  {TimestampUtc.ToLocalTime():HH:mm:ss}{(Exited ? "" : "  未确认退出")}";
}

public class FeedbackEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string ProcessName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Publisher { get; set; } = "";
    public ProcessCategory ReportedCategory { get; set; }
    public string UserNote { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending/applied
    public string Scope { get; set; } = "process"; // process/software
    public bool MarkCleanable { get; set; }
    public bool MarkKeep { get; set; }
    public bool MarkSystem { get; set; }
}

public class OnlineLookupResult
{
    public string Status { get; set; } = "unknown"; // found/uncertain/not_found/error/offline
    public string Summary { get; set; } = "";
    public ProcessCategory? SuggestedCategory { get; set; }
    public string SourceUrl { get; set; } = "";
    public Dictionary<string, string> Evidence { get; set; } = new();
}

public class SystemMemoryInfo
{
    public long TotalPhysicalBytes { get; set; }
    public long AvailablePhysicalBytes { get; set; }
    public long TotalCommitLimitBytes { get; set; }
    public long TotalCommittedBytes { get; set; }
    public long CachedBytes { get; set; }
    public double MemoryLoadPercent { get; set; }
}

public class GpuAdapterInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public long DedicatedVideoMemoryBytes { get; set; }
    public long UsedVideoMemoryBytes { get; set; }
    public double UsedPercent => DedicatedVideoMemoryBytes <= 0 ? 0 : (double)UsedVideoMemoryBytes / DedicatedVideoMemoryBytes * 100;
    public string Summary => DedicatedVideoMemoryBytes <= 0
        ? Name
        : $"{Name}  {FormatBytes(UsedVideoMemoryBytes)} / {FormatBytes(DedicatedVideoMemoryBytes)} ({UsedPercent:F0}%)";

    private static string FormatBytes(long b)
    {
        if (b < 1024) return b + " B";
        double v = b;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }
}

public class SuspendRecord
{
    public int Pid { get; set; }
    public DateTime StartTime { get; set; }
    public string Name { get; set; } = "";
    public DateTime SuspendedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = "";
    public string Display => $"{Name} (PID {Pid})  挂起于 {SuspendedAtUtc.ToLocalTime():HH:mm:ss}  {Reason}";
}

// ---- 腾挪工作流 (make room for a big allocation, e.g. AI generation) ----

public class MakeRoomRequest
{
    public double TargetAvailableGB { get; set; } = 8;
    public bool EnsureCommitToo { get; set; } = true;
    public double TargetCommitFreeGB { get; set; } = 8;
    public bool AllowKill { get; set; } = false;   // L4 fallback for headless processes
    public bool IncludeTrim { get; set; } = true;  // append L1 trim for the remainder
}

public class MakeRoomPlanItem
{
    public ProcessInfo Process { get; set; } = new();
    public CleanupActionLevel Action { get; set; }
    public long EstimatedPhysicalReclaimBytes { get; set; }
    public long EstimatedCommitReclaimBytes { get; set; }
    public string Rationale { get; set; } = "";
    /// <summary>For L3 items: if the graceful close times out, escalate to L4.</summary>
    public bool KillIfCloseFails { get; set; }
    /// <summary>Processes this item's process is a parent of — closing it affects them too.</summary>
    public int ChildCount { get; set; }
    public string ActionText => Action switch
    {
        CleanupActionLevel.L1Trim => "L1 回收工作集",
        CleanupActionLevel.L2Suspend => "L2 挂起",
        CleanupActionLevel.L3GracefulClose => "L3 优雅关闭",
        CleanupActionLevel.L4ForceKill => "L4 强制结束",
        _ => Action.ToString()
    };
}

public class MakeRoomPlan
{
    public List<MakeRoomPlanItem> Items { get; set; } = new();
    public long AvailableBeforeBytes { get; set; }
    public long CommitFreeBeforeBytes { get; set; }
    public long PhysicalGapBytes { get; set; }
    public long CommitGapBytes { get; set; }
    public long EstimatedPhysicalTotalBytes => Items.Sum(i => i.EstimatedPhysicalReclaimBytes);
    public long EstimatedCommitTotalBytes => Items.Sum(i => i.EstimatedCommitReclaimBytes);
    public bool CoversPhysicalGap => EstimatedPhysicalTotalBytes >= PhysicalGapBytes;
    public bool CoversCommitGap => !Items.Select(_ => 0).Any() || EstimatedCommitTotalBytes >= CommitGapBytes;
    public string SummaryText =>
        $"可用内存 {AvailableBeforeBytes / 1073741824.0:F1} GB，提交空闲 {CommitFreeBeforeBytes / 1073741824.0:F1} GB；" +
        $"计划 {Items.Count} 项，预计释放物理 {EstimatedPhysicalTotalBytes / 1073741824.0:F1} GB / 提交 {EstimatedCommitTotalBytes / 1073741824.0:F1} GB";
}

public class MakeRoomResult
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public long AvailableBeforeBytes { get; set; }
    public long AvailableAfterBytes { get; set; }
    public long CommitFreeBeforeBytes { get; set; }
    public long CommitFreeAfterBytes { get; set; }
    public List<string> Lines { get; set; } = new();
    public string SummaryText =>
        $"物理可用 {AvailableBeforeBytes / 1073741824.0:F1} GB → {AvailableAfterBytes / 1073741824.0:F1} GB（{(AvailableAfterBytes - AvailableBeforeBytes) / 1073741824.0:+0.0;-0.0} GB），" +
        $"提交空闲 {CommitFreeBeforeBytes / 1073741824.0:F1} GB → {CommitFreeAfterBytes / 1073741824.0:F1} GB";
}

public class AppConfig
{
    public AppMode CurrentMode { get; set; } = AppMode.Resident;
    public int RefreshIntervalSeconds { get; set; } = 10;
    public int UnusedCpuThresholdPercent { get; set; } = 2;
    public int UnusedIdleMinutes { get; set; } = 10;
    public int UnusedNoIoSeconds { get; set; } = 60;
    public long AiModeMinAvailableBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public long AiModeMinCommitFreeBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public List<string> Whitelist { get; set; } = new();
    public List<string> Blacklist { get; set; } = new();
    public List<string> ProtectedNames { get; set; } = new();
    public string OnlineLookupEndpoint { get; set; } = "";
    public string OnlineLookupApiKey { get; set; } = "";
    public bool OnlineLookupEnabled { get; set; } = true;
    public bool AutoModeSwitch { get; set; } = false;
    public int MaxCpuPercentForResident { get; set; } = 5;
    // Auto optimization (system-level memory lists)
    public bool AutoOptimizeEnabled { get; set; } = false;
    // 垃圾驻守清扫：自动结束无窗口/无音频/低负载的 Bloatware 与孤儿残留（默认关闭）
    public bool AutoJunkSweepEnabled { get; set; } = false;
    public bool AutoOptimizeIntervalEnabled { get; set; } = false;
    public int AutoOptimizeIntervalMinutes { get; set; } = 30;
    public int AutoOptimizeThresholdPercent { get; set; } = 15; // clean when free RAM below this %
    public bool AutoOptimizeOnLowMemory { get; set; } = true;
    public bool SkipOptimizeInFullscreen { get; set; } = true;
    // Auto per-process cleanup driven by the active mode profile
    public bool AutoCleanupEnabled { get; set; } = false;
    public bool AutoCloseCleanMatches { get; set; } = false; // graceful-close clean-pattern matches automatically
    // Safety guards for unused detection / auto actions
    public int MinProcessAgeSeconds { get; set; } = 120;      // never act on very young processes
    public int RequiredIdleObservations { get; set; } = 6;    // consecutive idle refreshes before candidacy
    public int GracefulCloseTimeoutSeconds { get; set; } = 8;
    // Tray / misc
    public bool ShowMemoryInTray { get; set; } = true;
    public bool GlobalHotkeyEnabled { get; set; } = true;
    public string GlobalHotkey { get; set; } = "CTRL+SHIFT+M";
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = false;
    public bool WriteEventLogAudit { get; set; } = false;
    public int UsageRetentionDays { get; set; } = 30;
    public double CompactLeft { get; set; } = -1;
    public double CompactTop { get; set; } = -1;
    public bool ResumeSuspendedOnModeChange { get; set; } = false;
    public Dictionary<AppMode, ModeProfile> ModeProfiles { get; set; } = new();
}

public class ModeProfile
{
    public AppMode Mode { get; set; }
    public string Name { get; set; } = "";
    public List<string> KeepPatterns { get; set; } = new();
    public List<string> CleanPatterns { get; set; } = new();
    public Dictionary<ProcessCategory, int> Priority { get; set; } = new();
    public long TriggerMemoryPressureBytes { get; set; }
    public List<CleanupActionLevel> AllowedActions { get; set; } = new();
    public bool AutoSwitchOnForeground { get; set; }
}
