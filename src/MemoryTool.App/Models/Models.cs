namespace MemoryTool.Models;

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
    SecuritySoftware
}

public enum ProcessRiskLevel { Low, Medium, High, Critical }

public enum CleanupActionLevel { L0Monitor, L1Trim, L2Suspend, L3SuggestClose, L4ForceKill }

public enum AppMode { Resident, Development, Game, AiGeneration, Balanced }

public class MemoryMetrics
{
    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public long CommitBytes { get; set; }          // pagefile usage
    public long PeakWorkingSetBytes { get; set; }
    public long SharedCommitBytes { get; set; }
    public long PrivateWorkingSetBytes { get; set; }
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
    public bool IsSystemProcess { get; set; }
    public bool IsService { get; set; }
    public bool IsStartupItem { get; set; }
    public bool IsScheduledTask { get; set; }
    public string ServiceName { get; set; } = "";
    public bool HasVisibleWindow { get; set; }
    public string MainWindowTitle { get; set; } = "";
    public DateTime StartTime { get; set; }
    public TimeSpan IdleTime { get; set; }
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
    public double IdleMinutes { get; set; }
    public bool IsUnusedCandidate { get; set; }
    public long EstimatedReclaimableBytes { get; set; }
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
    public ProcessRiskLevel Risk { get; set; }
    public CleanupActionLevel SuggestedLevel { get; set; }
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
    public Dictionary<string,string> Evidence { get; set; } = new();
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

public class AppConfig
{
    public AppMode CurrentMode { get; set; } = AppMode.Resident;
    public int RefreshIntervalSeconds { get; set; } = 5;
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
    // Auto optimization settings (borrowed from WinMemoryCleaner)
    public bool AutoOptimizeEnabled { get; set; } = false;
    public int AutoOptimizeIntervalMinutes { get; set; } = 30;
    public int AutoOptimizeThresholdPercent { get; set; } = 20; // clean when free RAM below this %
    public bool AutoOptimizeOnLowMemory { get; set; } = true;
    public bool ShowMemoryInTray { get; set; } = true;
    public bool GlobalHotkeyEnabled { get; set; } = true;
    public string GlobalHotkey { get; set; } = "CTRL+SHIFT+M";
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
