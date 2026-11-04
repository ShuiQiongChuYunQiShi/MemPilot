using MemPilot.Helpers;
using MemPilot.Services;   // PersistedIds（v0.10.0：持久化用稳定 id）

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

/// <summary>用途维度（v0.3.1）：从"进程身份"派生"它在给你干什么"。
/// 与 Category 不同：Category 是身份（游戏/开发工具/容器…），Workload 是用途（娱乐/工作/AI/系统/其他）。
/// 歧义场景（浏览器、通用三方）保持 Unknown，不猜。</summary>
public enum WorkloadKind
{
    Unknown = 0,
    Work = 1,
    Entertainment = 2,
    Ai = 3,
    System = 4,
    Other = 5,
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
    /// <summary>程序清单命中：所属程序显示名与发布者（展示用，不构成处置依据）。</summary>
    public string ProgramDisplayName { get; set; } = "";
    public string ProgramPublisher { get; set; } = "";
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
    public WorkloadKind Workload { get; set; } = WorkloadKind.Unknown;
    public string WorkloadReason { get; set; } = "";
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
    /// <summary>窗口枚举降级时置位（此时 HasVisibleWindow 是保守假设，不是实测）。</summary>
    public bool WindowEnumDegraded { get; set; }
    public long MemoryGrowthBytes { get; set; }  // WS growth over the tracked window (leak watch)
    // 显存（v0.3.4，PDH \GPU Process Memory）：不可用与 0 字节必须区分——
    // 采集失败时 GpuMemoryAvailable=false，UI 显示 "-" 而不是假 0（与 GpuAdapterInfo 同一红线）。
    public long GpuDedicatedMemoryBytes { get; set; }
    public long GpuSharedMemoryBytes { get; set; }
    public bool GpuMemoryAvailable { get; set; }
    public string GpuMemoryText => !GpuMemoryAvailable ? "-" : ByteFormat.Human(GpuDedicatedMemoryBytes);
    public int TreeDepth { get; set; }          // tree view: 0 = root (runtime-only)
    public bool HasChildren { get; set; }       // tree view: has in-set children
    public bool Collapsed { get; set; }         // tree view: node is collapsed in the UI

    /// <summary>Stable identity across refreshes: pid + start time defeats PID reuse.</summary>
    public (int pid, DateTime start) StateKey => (Pid, StartTime);
}

/// <summary>
/// 用途枚举的双向映射（单一来源：WorkloadClassifier.DisplayName 也走这里）。
/// v0.10.0：新增 <see cref="IdOf"/>——持久化只存稳定 id，显示文本可随语言变化。
/// </summary>
public static class WorkloadText
{
    public static string Of(WorkloadKind kind) => kind switch
    {
        WorkloadKind.Work => "工作",
        WorkloadKind.Entertainment => "娱乐",
        WorkloadKind.Ai => "AI",
        WorkloadKind.System => "系统",
        WorkloadKind.Other => "其他",
        _ => "未知",
    };

    /// <summary>稳定 id（写进 config.json 的那个值，永不随语言变化）。</summary>
    public static string IdOf(WorkloadKind kind) => kind switch
    {
        WorkloadKind.Work => PersistedIds.Workload.Work,
        WorkloadKind.Entertainment => PersistedIds.Workload.Entertainment,
        WorkloadKind.Ai => PersistedIds.Workload.Ai,
        WorkloadKind.System => PersistedIds.Workload.System,
        WorkloadKind.Other => PersistedIds.Workload.Other,
        _ => PersistedIds.Workload.Unknown,
    };

    /// <summary>id → 枚举（无法识别返回 null）。</summary>
    public static WorkloadKind? FromId(string? id) => id switch
    {
        PersistedIds.Workload.Work => WorkloadKind.Work,
        PersistedIds.Workload.Entertainment => WorkloadKind.Entertainment,
        PersistedIds.Workload.Ai => WorkloadKind.Ai,
        PersistedIds.Workload.System => WorkloadKind.System,
        PersistedIds.Workload.Other => WorkloadKind.Other,
        PersistedIds.Workload.Unknown => WorkloadKind.Unknown,
        _ => null,
    };
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
    /// <summary>用途标记（v0.4.0）：右键「标记用途」写入；null = 未标记，走规则判定。</summary>
    public WorkloadKind? ReportedWorkload { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string WorkloadMarkText => ReportedWorkload is { } w ? WorkloadText.Of(w) : "";

    // 展示用（v0.3.7）：表格里显示本地时间与中文范围/效果，而不是原始 UTC 与枚举名
    [System.Text.Json.Serialization.JsonIgnore]
    public string TimeText => TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm");
    [System.Text.Json.Serialization.JsonIgnore]
    public string ScopeText => Scope == "software" ? "软件" : "进程";
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectText => MarkSystem ? "标记为系统（受保护）"
                               : MarkKeep ? "需保留（不进清理）"
                               : MarkCleanable ? "可清理（优先候选）"
                               : "仅修正分类";
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

/// <summary>已安装程序条目（程序清单 v0.3.0）：注册表 Uninstall / 开始菜单 / 安装根扫描三源合并。</summary>
public class InstalledProgram
{
    public string DisplayName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string Source { get; set; } = ""; // registry / startmenu / scan
}

public class GpuAdapterInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public long DedicatedVideoMemoryBytes { get; set; }
    public long UsedVideoMemoryBytes { get; set; }
    /// <summary>
    /// v0.10.0：本进程在该适配器上的显存用量（IDXGIAdapter3::QueryVideoMemoryInfo 的 CurrentUsage）。
    /// **只用于诊断，永不当适配器总用量展示**——该 API 的语义是 per-process，不是整卡。
    /// </summary>
    public long SelfProcessUsageBytes { get; set; }
    /// <summary>用量来源：pdh / ""（不可用）。DXGI 不再提供适配器用量（见 v0.10.0 修复）。</summary>
    public string UsageSource { get; set; } = "";
    /// <summary>DXGI LUID（可空）：多适配器时把 PDH 的 luid 实例映射回具体卡。</summary>
    public uint LuidLowPart { get; set; }
    public int LuidHighPart { get; set; }
    public double UsedPercent => DedicatedVideoMemoryBytes <= 0 ? 0 : (double)UsedVideoMemoryBytes / DedicatedVideoMemoryBytes * 100;
    public string Summary => DedicatedVideoMemoryBytes <= 0
        ? Name
        : UsedVideoMemoryBytes <= 0
            ? $"{Name}  {ByteFormat.Human(DedicatedVideoMemoryBytes)}（用量不可用）"
            : $"{Name}  {ByteFormat.Human(UsedVideoMemoryBytes)} / {ByteFormat.Human(DedicatedVideoMemoryBytes)} ({UsedPercent:F0}%)";

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
    // ⚠️ v0.10.0 起为**废弃字段**：`ResidentEngine.AutoOptimize` 从未被 `AfterRefresh` 调用过，
    // 设置页那两个复选框点了没有任何效果（决策日志里 215 条 AutoOptimize 全是更早期的实现留下的）。
    // 保留字段只为能反序列化旧 config.json 而不报错；新代码一律读 CommitRelief 相关配置。
    public bool AutoOptimizeEnabled { get; set; } = false;
    // 垃圾驻守清扫：自动结束无窗口/无音频/低负载的 Bloatware 与孤儿残留（默认关闭）
    public bool AutoJunkSweepEnabled { get; set; } = false;
    // 低风险模式：自动清扫只处理"宿主已退出的孤儿残留"，不动常驻垃圾（更保守）
    public bool AutoJunkSweepOrphansOnly { get; set; } = false;
    // 提交内存兜底（v0.5.0）：提交余量 <5%（或 10 分钟涨 ≥8GB 且余量 <25%）时，
    // 先清低优先级备用列表；2 分钟后仍告急才压缩"安全子集"（绝不碰工作/游戏/前台/忙碌进程）。
    // v0.10.0：默认改为 **false**。理由：这是唯一会"不打招呼就压缩别人工作集"的自动行为
    // （压缩会让下次访问重新读盘、卡顿一秒）。开源后陌生人装上就该什么都不做，
    // 让他看完说明再自己打开。已有的 config.json 里这个键已存在，改默认值不会影响老用户。
    public bool AutoCommitReliefEnabled { get; set; } = false;
    // 提交告急时是否允许「征得同意后优雅关闭闲置后台」来真正释放提交内存。
    // 默认关：L3 会关掉程序，必须由用户逐次确认（压缩/清缓存已被实测证明降不了提交）。
    public bool CommitReliefL3Enabled { get; set; } = false;
    // 租约 API（v0.7.0）：本地 HTTP + Bearer 令牌，AI 任务开跑前申请内存、结束归还。
    // 只绑 127.0.0.1；令牌落盘 lease-token.txt，设置页可见/可重新生成。
    // v0.10.0：默认改为 **false**。理由：一个陌生人装上就监听端口、而且还能被本地任意进程
    // （读到令牌文件即可）要求挂起进程，这是"能力默认关闭"的基本原则。要用的人在设置页打开。
    public bool LeaseApiEnabled { get; set; } = false;
    public int LeaseApiPort { get; set; } = 52717;
    // 重要提示触达（托盘气泡 + 主页横幅）；false = 仅写决策日志
    public bool ShowAlerts { get; set; } = true;
    // 场景识别影子模式：只记录"如果自动切换会切到哪"，不动作（默认开，校准期用）
    public bool SceneShadowEnabled { get; set; } = true;
    // 进程页列设置：用户隐藏的列（**稳定 id**，见 `PersistedIds.Column`；v0.10.0 起不再存中文表头，
    // 否则加语言会让设置静默重置）。默认收起 9 个低频/证据列——19 列合计约 1.7k px，
    // 1280 宽窗口下会把「签名/分类依据」挤到视口外，等于把判断依据藏起来（棱镜 ui/table-density）。
    // PID 与名称固定可见（DataGrid FrozenColumnCount=2），不在此列表中生效。
    public List<string> ProcessHiddenColumns { get; set; } = new()
    {
        PersistedIds.Column.MemoryGrowth, PersistedIds.Column.ChildCount,
        PersistedIds.Column.Threads, PersistedIds.Column.WindowTitle,
        PersistedIds.Column.Vendor, PersistedIds.Column.Audio,
        PersistedIds.Column.ModeKeep, PersistedIds.Column.Signature,
        PersistedIds.Column.CategoryReason
    };
    // 进程页用途筛选："" = 全部；否则为 **稳定 id**（work/entertainment/ai/system/other/unknown，
    // 见 `PersistedIds.Workload`）。v0.10.0 起不再存中文用途名。
    public string WorkloadFilter { get; set; } = "";
    // 显存采样（v0.3.4）：独立 30 秒节奏，与内存采样分开落盘（gpu-*.jsonl）。
    // 为什么单独高频：AI 生成视频时显存尖峰只有 1–2 分钟，5 分钟采样会漏掉。
    public bool GpuSamplingEnabled { get; set; } = true;
    public int GpuSampleIntervalSeconds { get; set; } = 30;   // 允许 15–300；0 视为关闭
    // ⚠️ 以下四项均为废弃字段（v0.10.0），只为兼容旧 config.json 的反序列化保留，无任何代码读取。
    public bool AutoOptimizeIntervalEnabled { get; set; } = false;
    public int AutoOptimizeIntervalMinutes { get; set; } = 30;
    public int AutoOptimizeThresholdPercent { get; set; } = 15;
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
    /// <summary>首次运行说明横幅是否已被用户关闭（v0.10.0）。</summary>
    public bool FirstRunNoticeDismissed { get; set; } = false;
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
