using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    public ProcessService ProcessService { get; } = new();
    public ServiceService ServiceService { get; } = new();
    public AudioSessionService AudioService { get; } = new();
    public GpuInfoService GpuService { get; } = new();
    /// <summary>显存读取（PDH，逐进程 + 适配器）；GpuMonitor 内另有一份用于独立采样。</summary>
    public GpuMemoryService GpuMemory { get; } = new();
    public GpuMonitor GpuMonitor { get; }
    public ClassificationEngine Classification { get; }
    public UnusedDetector UnusedDetector { get; } = new();
    public OnlineLookupService OnlineLookup { get; } = new();
    public ModeManager ModeManager { get; }
    public CleanupEngine Cleanup { get; }
    public ResidentEngine Engine { get; }
    public DecisionLog Decisions { get; }
    public UsageRecorder Usage { get; }
    /// <summary>自身开销记录（v0.18.0）：调度内存的工具必须能自证开销。</summary>
    public SelfCostRecorder SelfCost { get; }
    public BehaviorLedger Behaviors { get; }
    public ConfigStore ConfigStore { get; }
    public AuditLogger Audit { get; }
    public FeedbackStore Feedback { get; }

    private ObservableCollection<ProcessInfo> _processes = new();
    public ObservableCollection<UnusedCandidate> UnusedCandidates { get; } = new();
    public ObservableCollection<AuditEntry> AuditEntries { get; } = new();
    public ObservableCollection<FeedbackEntry> FeedbackEntries { get; } = new();
    public ObservableCollection<SuspendRecord> SuspendedProcesses { get; } = new();
    private ObservableCollection<DecisionEntry> _decisionEntries = new();
    public ObservableCollection<DecisionEntry> DecisionEntries
    {
        get => _decisionEntries;
        private set { _decisionEntries = value; OnPropertyChanged(); }
    }
    private ObservableCollection<DecisionEntry> _homeDecisions = new();
    public ObservableCollection<DecisionEntry> HomeDecisions
    {
        get => _homeDecisions;
        private set { _homeDecisions = value; OnPropertyChanged(); }
    }

    private bool _showNoise;
    /// <summary>
    /// 决策日志是否显示「建议/增长观察」噪声（v0.20.0，默认折叠）。
    /// 实测 11 天 5,000 条记录中 JunkAdvice 占 87.7%——用户要滚过几百条建议才能看到真实动作。
    /// 注意挂起/自动恢复属于真实动作，不受此开关影响（判定见 DecisionLogFilter）。
    /// </summary>
    public bool ShowNoise
    {
        get => _showNoise;
        set
        {
            if (_showNoise == value) return;
            _showNoise = value;
            OnPropertyChanged();
            ReloadDecisions();
        }
    }

    private string _decisionEmptyText = UiStrings.Get("Empty_Decisions");
    /// <summary>
    /// 决策日志空状态文案（v0.22.0）：有记录但被折叠/筛选隐藏时，
    /// 不能再说"还没有记录"——那会把"用户自己折叠了"误导成"什么都没发生"。
    /// </summary>
    public string DecisionEmptyText
    {
        get => _decisionEmptyText;
        private set { _decisionEmptyText = value; OnPropertyChanged(); }
    }

    /// <summary>按"总记录数 vs 当前显示数"更新决策页空状态文案。</summary>
    private void UpdateDecisionEmptyText(int recentCount, int shownCount)
        => DecisionEmptyText = recentCount > 0 && shownCount == 0
            ? UiStrings.F("Decisions_HiddenByFilter", recentCount)
            : UiStrings.Get("Empty_Decisions");

    /// <summary>决策日志 Kind 下拉项（v0.22.0）。ToString 返回显示名——自定义 ComboBox 模板
    /// 的 SelectionBox 走 ContentPresenter，不依赖 DisplayMemberPath。</summary>
    public sealed record DecisionKindOption(string Kind, string Display)
    {
        public override string ToString() => Display;
    }

    public ObservableCollection<DecisionKindOption> DecisionKindOptions { get; } = new();

    private string _selectedDecisionKind = DecisionLogFilter.AllKinds;
    /// <summary>决策日志 Kind 筛选（v0.22.0）：空串 = 全部。与 ShowNoise 是两个独立过滤（AND）。</summary>
    public string SelectedDecisionKind
    {
        get => _selectedDecisionKind;
        set
        {
            var v = value ?? DecisionLogFilter.AllKinds;
            if (_selectedDecisionKind == v) return;
            _selectedDecisionKind = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDecisionKindOption));
            ReloadDecisions();
        }
    }

    /// <summary>
    /// 下拉框的选中项（v0.22.0）。用 SelectedItem 而不是 SelectedValue：
    /// 后者在初始值 `""` 时不会与第一项（Kind=""）匹配，下拉框会显示空白。
    /// </summary>
    public DecisionKindOption? SelectedDecisionKindOption
    {
        get => DecisionKindOptions.FirstOrDefault(o => o.Kind == _selectedDecisionKind)
               ?? DecisionKindOptions.FirstOrDefault();
        set
        {
            var v = value?.Kind ?? DecisionLogFilter.AllKinds;
            if (_selectedDecisionKind == v) return;
            _selectedDecisionKind = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDecisionKindOption));
            ReloadDecisions();
        }
    }

    /// <summary>重建 Kind 下拉：包含当前日志里出现过的全部 Kind（按条数降序），首项"全部类型"。</summary>
    private void RebuildDecisionKindOptions()
    {
        var current = _selectedDecisionKind;
        DecisionKindOptions.Clear();
        DecisionKindOptions.Add(new DecisionKindOption(DecisionLogFilter.AllKinds,
            UiStrings.Get("Decisions_KindFilterAll")));
        foreach (var kv in Decisions.CountByKind()
                     .Where(kv => !string.IsNullOrEmpty(kv.Key))
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            DecisionKindOptions.Add(new DecisionKindOption(kv.Key, UiTerms.OfKind(kv.Key)));

        // 旧选择已不在列表里（日志轮转/清空）→ 回到"全部"
        if (!DecisionKindOptions.Any(o => o.Kind == current))
        {
            _selectedDecisionKind = DecisionLogFilter.AllKinds;
            OnPropertyChanged(nameof(SelectedDecisionKind));
        }
        OnPropertyChanged(nameof(SelectedDecisionKindOption));
    }
    public ObservableCollection<DisposalRecord> DisposalRecords { get; } = new();

    private SystemMemoryInfo _sysMem = new();
    public SystemMemoryInfo SysMem
    {
        get => _sysMem;
        private set
        {
            _sysMem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalMemoryText));
            OnPropertyChanged(nameof(AvailableMemoryText));
            OnPropertyChanged(nameof(CommitText));
            OnPropertyChanged(nameof(CommitFreeText));
            OnPropertyChanged(nameof(UsedPercent));
            OnPropertyChanged(nameof(UsedPercentText));
            OnPropertyChanged(nameof(UsedDetailText));
        }
    }

    private bool _aiSessionActive;
    public bool AiSessionActive { get => _aiSessionActive; private set { _aiSessionActive = value; OnPropertyChanged(); } }
    private string _aiSessionText = UiStrings.Get("Home_NoAiSession");
    public string AiSessionText { get => _aiSessionText; private set { _aiSessionText = value; OnPropertyChanged(); } }

    /// <summary>租约管理器 + 本地 API（v0.7.0）。</summary>
    public LeaseManager LeaseManager { get; }
    public LeaseApiService LeaseApi { get; }
    private string _leaseToken = "";
    public string LeaseToken => _leaseToken;

    private List<GpuAdapterInfo> _gpu = new();
    private GpuMemorySnapshot? _lastGpuMem;
    public string GpuSummaryText => _gpu.Count == 0 ? UiStrings.Get("Home_NoGpu") : string.Join("  |  ", _gpu.Select(g => g.Summary));

    /// <summary>自身开销一行（主页卡片，v0.18.0）。数据来自 SelfCostRecorder 的分钟聚合，
    /// 读的是内存预热值，不落每次刷新读盘。</summary>
    public string SelfCostText
    {
        get
        {
            var s = SelfCost.Latest();
            if (s == null) return UiStrings.F("SelfCost_Collecting", ResidentEngine.LightProbeIntervalSeconds);
            var text = UiStrings.F("SelfCost_Summary", s.Rss, s.Cpu, s.P95, s.N);
            if (s.Fails > 0) text += UiStrings.F("SelfCost_StageFailures", s.Fails);
            return text;
        }
    }

    /// <summary>显存用量一行（主页 GPU 卡第二行）：已用/总量（百分比）· 来源。不可用就说不可用。</summary>
    public string GpuMemorySummaryText
    {
        get
        {
            var snap = _lastGpuMem;
            if (snap == null || !snap.Available) return "";
            var used = snap.UsedBytes ?? 0;
            var total = _gpu.Sum(a => a.DedicatedVideoMemoryBytes);
            var src = snap.Source switch
            {
                "pdh-adapter" => UiStrings.Get("Gpu_SourcePdhAdapter"),
                "pdh-process" => UiStrings.Get("Gpu_SourcePdhProcess"),
                _ => "DXGI"   // v0.10.0：DXGI 已不再提供适配器用量，理论上不会走到这里
            };
            return total > 0
                ? UiStrings.F("Gpu_UsageWithTotal", ByteFormat.Human(used), ByteFormat.Human(total), used * 100.0 / total, src)
                : UiStrings.F("Gpu_UsageOnly", ByteFormat.Human(used), src);
        }
    }

    public string TotalMemoryText => FormatBytes(SysMem.TotalPhysicalBytes);
    public string AvailableMemoryText => FormatBytes(SysMem.AvailablePhysicalBytes);
    public string CommitText => $"{FormatBytes(SysMem.TotalCommittedBytes)} / {FormatBytes(SysMem.TotalCommitLimitBytes)}";
    public string CommitFreeText => FormatBytes(SysMem.TotalCommitLimitBytes - SysMem.TotalCommittedBytes);
    public double UsedPercent => SysMem.TotalPhysicalBytes <= 0 ? 0 : SysMem.MemoryLoadPercent;
    public string UsedPercentText => $"{UsedPercent:F0}%";
    public string UsedDetailText => UiStrings.F("Home_UsedOfTotal",
        FormatBytes(SysMem.TotalPhysicalBytes - SysMem.AvailablePhysicalBytes), TotalMemoryText);
    /// <summary>左下角「内存」标签（v0.19.0，随语言切换）。</summary>
    public string MemoryLabel => UiStrings.Get("Home_MemoryLabel");

    private string _candidateRuleText = "";
    public string CandidateRuleText
    {
        get => _candidateRuleText;
        private set { _candidateRuleText = value; OnPropertyChanged(); }
    }

    private AppMode _currentMode = AppMode.Resident;
    private DateTime _lastModeSwitchUtc = DateTime.MinValue;
    private string _lastModeTarget = "";
    private readonly List<DateTime> _modeSwitchTimes = new();

    public AppMode CurrentMode
    {
        get => _currentMode;
        set => SetMode(value, "ui");
    }

    /// <summary>
    /// 模式切换统一入口（v0.5.0）：带来源归因（ui / startup / engine），并抑制 60 秒内同目标重复切换。
    /// 数据依据：51 条审计全标 Manual、其中 15 次挤在 20 分钟内——无法归因就无法判断是 UI 误触还是别处调用。
    /// startup 是「恢复上次模式」，不是一次切换：只更新字段与通知，不写审计、不落盘、不触发挂起恢复。
    /// </summary>
    public void SetMode(AppMode mode, string source)
    {
        if (_currentMode == mode) return;

        if (source == "startup")
        {
            _currentMode = mode;
            _lastModeTarget = mode.ToString();
            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CurrentModeText));
            OnPropertyChanged(nameof(ModeDescription));
            return;
        }

        var now = DateTime.UtcNow;
        var decision = ModeSwitchPolicy.Decide(_lastModeTarget, mode.ToString(), _lastModeSwitchUtc, now, _modeSwitchTimes);
        bool engineDriven = source == "engine";

        // 引擎侧已有自己的滞回（连续 2 轮 + 60s 冷却），这里不再抑制，只记录；
        // UI 侧重复点击同一目标则忽略并留痕（真机密集簇的来源待这条日志确认）。
        if (!engineDriven && !decision.Apply)
        {
            Decisions.Log(new DecisionEntry
            {
                Kind = "ModeSwitchSuppressed",
                Target = mode.ToString(),
                Decision = "忽略",
                Reason = $"同一目标 60 秒内重复切换（来源 {source}）"
            });
            return;
        }

        var previous = _currentMode;
        _currentMode = mode;
        _lastModeSwitchUtc = now;
        _lastModeTarget = mode.ToString();
        _modeSwitchTimes.Add(now);
        if (_modeSwitchTimes.Count > 20) _modeSwitchTimes.RemoveRange(0, _modeSwitchTimes.Count - 20);

        if (!engineDriven)
        {
            ConfigStore.Config.CurrentMode = mode;
            ConfigStore.Save();
        }

        Audit.Log(new AuditEntry
        {
            Action = "ModeSwitch",
            Target = $"{previous} → {mode}",
            Level = engineDriven ? "Auto" : "Manual",
            Result = source
        });

        if (decision.Storm)
            Decisions.Log(new DecisionEntry
            {
                Kind = "ModeSwitchStorm",
                Target = mode.ToString(),
                Decision = "提示",
                Reason = $"10 分钟内切换超过 {ModeSwitchPolicy.StormThreshold} 次（来源 {source}）"
            });

        OnPropertyChanged(nameof(CurrentMode));
        OnPropertyChanged(nameof(CurrentModeText));
        OnPropertyChanged(nameof(ModeDescription));

        // 引擎侧自己已经做过挂起恢复（ApplySceneDecision），这里只处理 UI 切换
        if (!engineDriven && ConfigStore.Config.ResumeSuspendedOnModeChange)
        {
            var records = Cleanup.Suspended.LiveRecords();
            foreach (var rec in records)
                Cleanup.Resume(rec.Pid, rec.Name);
            if (records.Count > 0)
            {
                SuspendedProcesses.Clear();
                Status = UiStrings.F("Tabs_ModeResumed", records.Count);
            }
        }
    }
    public string CurrentModeText => ModeManager.Profile.Name;
    public string AppVersionText => "v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?");

    public string ModeDescription => CurrentMode switch
    {
        AppMode.Resident => UiStrings.Get("Mode_ResidentDesc"),
        AppMode.Development => UiStrings.Get("Mode_DevelopmentDesc"),
        AppMode.Game => UiStrings.Get("Mode_GameDesc"),
        AppMode.AiGeneration => UiStrings.Get("Mode_AiDesc"),
        AppMode.Balanced => UiStrings.Get("Mode_BalancedDesc"),
        _ => ""
    };

    private string _status = UiStrings.Get("Common_Ready");
    /// <summary>
    /// 动作反馈（v0.11.0 P0-1）：写入即进入 60 秒保留窗口，刷新循环不会覆盖它。
    /// 绝大多数写入点（64 处）都是用户操作的结果，理应可见——只有刷新循环两处
    /// 改用 <see cref="RefreshStatus"/> 写入。
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetUi(() =>
        {
            _status = value;
            _actionStatus = value;
            _actionStatusUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(HasActionStatus));
        });
    }

    /// <summary>刷新循环专用：写周期性噪音文案，不进入动作保留窗口。</summary>
    public void RefreshStatus(string message) => SetUi(() =>
    {
        _status = message;
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(HasActionStatus));
    });

    // ---- 状态双轨（v0.11.0，UX 摸排 P0-1）----
    // 故障：刷新循环每 ≤10s 把 Status 覆写两次（"正在枚举…"/"已枚举 N 个…"），
    // 所有动作反馈（"已标记用途"/"恢复 N 个挂起"/错误）10 秒内被抹掉——用户根本看不到。
    // 现在动作反馈走 ActionStatus（保留到下一次动作或 60s），刷新噪音留在 Status；
    // 状态栏 UI 把 ActionStatus 显示为加粗前景段，Status 为次要段。
    private string _actionStatus = "";
    private DateTime _actionStatusUtc = DateTime.MinValue;
    /// <summary>动作反馈（用户操作的结果/错误）。比刷新文案优先显示，60 秒后自动让位。</summary>
    public string ActionStatus
    {
        get => _actionStatus;
        private set => SetUi(() => { _actionStatus = value; _actionStatusUtc = DateTime.UtcNow; OnPropertyChanged(nameof(HasActionStatus)); });
    }
    /// <summary>状态栏是否处于"动作反馈"态（决定 UI 用哪种前景色）。</summary>
    public bool HasActionStatus => _actionStatus.Length > 0 && (DateTime.UtcNow - _actionStatusUtc).TotalSeconds < 60;

    /// <summary>写一条动作反馈。之后 RefreshStatus 不再覆盖它（见 StatusDisplay）。</summary>
    public void SetActionStatus(string message) => ActionStatus = message;

    /// <summary>状态栏实际显示的文本：动作反馈优先（60 秒内），否则刷新文案。</summary>
    public string StatusDisplay => HasActionStatus ? ActionStatus : Status;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetUi(() => { _isBusy = value; OnPropertyChanged(nameof(BusyText)); });
    }
    public string BusyText => IsBusy ? UiStrings.Get("Common_Refreshing") : UiStrings.Get("Common_Idle");

    /// <summary>Raised after the UI collections were rebuilt; the view re-applies selection.</summary>
    public event EventHandler? CollectionsRebuilt;
    /// <summary>Raised after an audit write so the view can reload the audit tab.</summary>
    public event EventHandler? AuditChanged;

    private List<ProcessInfo> _lastProcs = new();
    /// <summary>快照时间（UTC）。/api/plan 靠它如实报告 snapshotAgeSeconds——
    /// plan 基于上一次 tick 的进程列表，不是实时枚举，隐瞒年龄会让 agent 高估可行量。</summary>
    private DateTime _lastProcsUtc = DateTime.MinValue;
    /// <summary>完整枚举次数。/api/plan 靠它判断 CPU/IO 采样是否预热：
    /// 首次枚举没有上一次的 CPU 时间基线，会把大量进程算成"忙"（真机 inUse 从 1 涨到 62），
    /// 于是 plan 报"什么都腾不出来"——方向保守但会误导 agent 放弃。</summary>
    private int _enumerationCount;

    /// <summary>Window visible? When hidden, refreshes skip rebuilding UI collections
    /// (tray lightweight mode — the engine keeps working, the UI stays cheap).</summary>
    public bool UiActive { get; set; } = true;

    public MainViewModel(ConfigStore config, AuditLogger audit, FeedbackStore feedback)
    {
        ConfigStore = config;
        Audit = audit;
        Feedback = feedback;
        _workloadFilter = config.Config.WorkloadFilter ?? "";
        GpuMonitor = new GpuMonitor(ConfigStore.DataDirectory, ConfigStore.Config.UsageRetentionDays);
        _workloadRules = WorkloadRules.LoadOrCreate(ConfigStore.DataDirectory);
        _leaseToken = LeaseTokenStore.LoadOrCreate(ConfigStore.DataDirectory);
        LeaseManager = new LeaseManager();
        LeaseApi = new LeaseApiService(
            () => ProcessService.GetSystemMemory(),
            ExecuteLeaseRelief,
            LeaseManager,
            () => _leaseToken,
            () => ConfigStore.Config.LeaseApiPort,
            BuildLeasePlan);
        _workloadLookup = p => Feedback.FindWorkload(p.Name, p.ExecutablePath);
        ModeManager = new ModeManager(config.Config);
        ProgramCatalog = ProgramCatalogService.TryLoad(Path.Combine(ConfigStore.DataDirectory, "programs.json"))
                         ?? new ProgramCatalogService();
        Classification = new ClassificationEngine(feedback, ProgramCatalog);
        UnusedDetector.Config = config.Config;
        OnlineLookup.Config = config.Config;
        Decisions = new DecisionLog(ConfigStore.DataDirectory, ConfigStore.Config.ArchiveRetentionMonths);
        Usage = new UsageRecorder(ConfigStore.DataDirectory, ConfigStore.Config.UsageRetentionDays);
        SelfCost = new SelfCostRecorder(ConfigStore.DataDirectory, ConfigStore.Config.UsageRetentionDays);
        Cleanup = new CleanupEngine(ProcessService, audit, ConfigStore.DataDirectory);
        Behaviors = BehaviorLedger.Load(Path.Combine(ConfigStore.DataDirectory, "behavior.json"));
        Engine = new ResidentEngine(config, Cleanup, new NativeMemoryCleanerService(), audit, Decisions, Usage, Behaviors);
        Engine.SelfCost = SelfCost;
        Engine.Alert += (title, message, warning) =>
        {
            if (title.Contains(UiStrings.Get("Habit_Period"))) SetUi(() => HabitHintText = message);
            RaiseAlert(title, message);            // 主页横幅（引擎线程 → SetUi 内部 marshal）
            AlertRaised?.Invoke(title, message, warning); // 托盘气泡（由 MainWindow 订阅）
        };
        Engine.ModeChanged += (s, e) =>
        {
            SetMode(e.Mode, "engine");
            ReloadDecisions();
        };

        // 归还/过期前先同步恢复挂起的进程（HTTP 响应要带恢复结果）
        LeaseManager.RestoreHook = RestoreLeaseSuspended;

        // v0.7.0：租约事件 → 决策日志 + 横幅/托盘（Start/Expire/Guard）
        LeaseManager.LeaseEvent += (lease, ev) => SetUi(() =>
        {
            double achieved = lease.AchievedAvailableGb;
            double peak = lease.TargetAvailableGb;
            switch (ev)
            {
                case "开始":
                    // v0.24.1：把申请方 PID 同步给引擎——自动兜底（FloorTrim / CommitRelief 的
                    // TrimSafe）也必须排除它及其子树，不能压缩"为工作让路"的那一方。
                    Engine.LeaseRequesterPid = lease.RequesterPid;
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "开始",
                        Reason = $"{lease.Requester} 申请 {lease.TargetAvailableGb:F1} GB（策略 {lease.Policy}，挂起 {lease.Suspended.Count} 个）；实测达成 {achieved:F1} GB（{lease.Details}）" });
                    RaiseAlert(UiStrings.Get("Lease_Active"), UiStrings.F("Lease_ActiveBody",
                        lease.Requester, achieved, peak, lease.ExpiresUtc.ToLocalTime().ToString("HH:mm")));
                    AlertRaised?.Invoke(UiStrings.Get("Lease_Active"), $"{achieved:F1} / {peak:F1} GB", false);
                    break;
                case "归还":
                    Engine.LeaseRequesterPid = 0;
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "归还",
                        Reason = $"{lease.Requester} 主动归还（占用 {(lease.EndUtc - lease.StartUtc)?.TotalMinutes:F0} 分钟，策略 {lease.Policy}）" });
                    RaiseAlert(UiStrings.Get("Lease_ReturnedTitle"), UiStrings.F("Lease_ReturnedBody", lease.Requester));
                    break;
                case "过期":
                    Engine.LeaseRequesterPid = 0;
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "过期",
                        Reason = $"TTL 到期自动结束（占用至 {lease.ExpiresUtc.ToLocalTime():HH:mm}，策略 {lease.Policy}）" });
                    RaiseAlert(UiStrings.Get("Lease_ExpiredTitle"), UiStrings.Get("Lease_ExpiredBody"));
                    break;
                case "守护告警":
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "守护告警",
                        Reason = lease.Details });
                    RaiseAlert(UiStrings.Get("Lease_ErodedTitle"), UiStrings.Get("Lease_ErodedBody"));
                    AlertRaised?.Invoke(UiStrings.Get("Lease_ErodedTitle"), lease.Details, true);
                    break;
            }
        });

        // v0.5.0：AI 会话只观测与记录，不做任何处置（处置留给下一轮租约）
        // v0.5.1：提交告急且用户允许时，征得同意后优雅关闭闲置后台（唯一能降低提交计数的动作）
        // v0.9.1：BeginInvoke 而不是 Invoke——引擎 tick 线程不能被 UI 阻塞
        // v0.11.0（UX 摸排 P1-3）：模态 MessageBox 改为**主页非模态确认卡**。
        // 模态框的问题：提交 <5% 时随时弹出抢焦点；无人值守时永远没人点，引擎只能一直等。
        // 现在渲染成确认卡（候选名单+确认/放弃），30 分钟冷却；用户不在场（托盘模式）则
        // 记决策日志后自动放弃——引擎从不被 UI 阻塞，安全确认依然存在。
        // v0.15.0：垃圾进程建议卡片接线 + 学习账本
        Engine.JunkAdvice = new JunkAdviceStore(ConfigStore.DataDirectory);
        Engine.MarkedCleanableProvider = () => Feedback.All()
            .Where(f => f.MarkCleanable && f.Status == "applied")
            .Select(f => f.ProcessName).ToList();   // v0.15.1：用户标记直接喂给建议器
        AttachJunkAdvice();
        AttachWslHint();

        Engine.CommitReliefL3Suggested += (candidates, reclaimBytes) =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (candidates.Count == 0) return;
                if (!UiActive)
                {
                    Decisions.Log(new DecisionEntry
                    {
                        Kind = "CommitRelief",
                        Target = "L3Request",
                        Decision = "跳过",
                        Reason = $"提交告急但用户不在场（托盘模式），已放弃本次优雅关闭请求（候选 {candidates.Count} 个，预计释放 {reclaimBytes / 1048576.0:F0} MB）"
                    });
                    return;
                }
                if ((DateTime.UtcNow - _lastL3CardUtc).TotalMinutes < 30)
                {
                    Decisions.Log(new DecisionEntry
                    {
                        Kind = "CommitRelief",
                        Target = "L3Request",
                        Decision = "跳过",
                        Reason = "确认卡冷却中（30 分钟内已请求过），未重复打扰"
                    });
                    return;
                }
                _lastL3CardUtc = DateTime.UtcNow;
                CommitReliefCandidates = candidates;
                CommitReliefReclaimMb = (long)(reclaimBytes / 1048576.0);
                CommitReliefPending = true;
            }));

        // v0.24.0：低可用兜底的阶段通知（温和完成 / 全量完成 / 询问继续 / 用户关闭后跳过）
        Engine.FloorTrim += notice => SetUi(() => HandleFloorTrim(notice));

        GpuMonitor.AiSessionChanged += (summary, started) => SetUi(() =>
        {
            // v0.25.0：可选自动预留（默认关）。放在会话状态同步之后，
            // 因为 HandleAutoLeaseForAi 会读 ConfigStore，不依赖 AiSessionActive。
            HandleAutoLeaseForAi(started);
            Engine.AiSessionActive = started;
            AiSessionActive = started;
            double peakGb = summary.PeakUsedBytes / 1073741824.0;
            double sharedGb = summary.PeakSharedBytes / 1073741824.0;
            if (started)
            {
                AiSessionText = UiStrings.F("Ai_InProgressText", peakGb, summary.DominantName);
                Decisions.Log(new DecisionEntry
                {
                    Kind = "AiSession",
                    Target = summary.DominantName,
                    Decision = "开始",
                    Reason = $"显存 {peakGb:F1} GB（共享 {sharedGb:F1} GB）；判定阈值 8GB、最近 5 个 30 秒样本中 ≥3 个"
                });
                RaiseAlert(UiStrings.Get("Ai_SessionTitle"), UiStrings.F("Ai_StartedBody", peakGb, summary.DominantName));
                AlertRaised?.Invoke(UiStrings.Get("Ai_SessionTitle"), UiStrings.F("Ai_ShortBody", peakGb, summary.DominantName), false);
            }
            else
            {
                AiSessionText = UiStrings.F("Ai_LastSessionText", summary.Duration.TotalMinutes, peakGb);
                Decisions.Log(new DecisionEntry
                {
                    Kind = "AiSession",
                    Target = summary.DominantName,
                    Decision = "结束",
                    Reason = $"时长 {summary.Duration.TotalMinutes:F0} 分钟；峰值显存 {peakGb:F1} GB（共享 {sharedGb:F1} GB）；" +
                             (summary.PeakCommitFreePct is { } cf ? $"峰值提交余量 {cf:F1}%；" : "") +
                             $"主导进程出现过 {summary.PidCount} 个 PID"
                });
                RaiseAlert(UiStrings.Get("Ai_SessionEnded"), UiStrings.F("Ai_EndedBody",
                    summary.Duration.TotalMinutes, peakGb, summary.DominantName));
            }
        });
        OnlineLookupService.LoadCache(ConfigStore.DataDirectory);
        ProgramCatalogInfo = ProgramCatalog.Count > 0
            ? UiStrings.F("Settings_CatalogInfo", ProgramCatalog.Count, ProgramCatalog.BuiltUtc.ToLocalTime().ToString("MM-dd HH:mm"))
            : UiStrings.Get("Settings_CatalogNotBuilt");
        if (ProgramCatalog.Count == 0) _ = Task.Run(BuildProgramCatalogInBackground);
        WindowsEventLog.Enabled = config.Config.WriteEventLogAudit;
        SetMode(config.Config.CurrentMode, "startup");
        foreach (var a in audit.Recent(200)) AuditEntries.Add(a);
        foreach (var d in DecisionLogFilter.Filter(Decisions.Recent(200), ShowNoise, SelectedDecisionKind)) DecisionEntries.Add(d);
        RebuildDecisionKindOptions();
        foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        foreach (var f in feedback.All()) FeedbackEntries.Add(f);
        ScheduleOrphanLeaseResume();
    }

    /// <summary>
    /// 租约的软腾挪执行（v0.7.0）：清低优先级备用列表 + 压缩"安全子集"（与提交兜底同一套排除规则，
    /// 绝不碰工作/游戏/前台/忙碌进程），返回**实测**前后可用内存。显存无法凭空释放，租约只承诺可用内存。
    /// </summary>
    /// <summary>
    /// v0.8.0：恢复硬租约挂起的进程。**同步执行**（由 LeaseManager.RestoreHook 在归还/过期前调用），
    /// 保证 HTTP 响应里带上恢复结果；UI 更新另外 marshal。
    /// </summary>
    private (int restored, int failed) RestoreLeaseSuspended(LeaseInfo lease)
    {
        if (lease.Suspended.Count == 0) return (0, 0);
        int restored = 0, failed = 0;
        foreach (var s in lease.Suspended)
        {
            try
            {
                if (s.Restored) { restored++; continue; }              // 避免重置已恢复的
                var live = Cleanup.Suspended.LiveRecords().Any(r => r.Pid == s.Pid);
                if (!live)
                {
                    s.RestoreNote = UiStrings.Get("Common_ProcessExited");
                    continue;
                }
                var r = Cleanup.Resume(s.Pid, s.Name, s.StartTime);
                s.Restored = r.Success;
                s.RestoreNote = r.Message;
                if (r.Success) restored++; else failed++;
            }
            catch (Exception ex) { failed++; s.RestoreNote = ex.Message; }
        }

        int total = lease.Suspended.Count;
        var failedPart = failed > 0 ? UiStrings.F("Lease_RestoreFailedPart", failed) : "";
        SetUi(() => Status = UiStrings.F("Lease_RestoreSummary", restored, failedPart));
        Decisions.Log(new DecisionEntry
        {
            Kind = "Lease", Target = lease.Id, Decision = "恢复",
            Reason = $"硬租约挂起的 {total} 个进程中恢复 {restored} 个" + (failed > 0 ? $"，失败 {failed}" : "")
        });
        return (restored, failed);
    }

    /// <summary>退出前释放资源（v0.3.8，棱镜 backend/resource-lifecycle）：
    /// 显存采样定时器 + PDH 查询句柄 + 引擎定时器。进程退出虽会回收，但 Dispose 存在就该有人调。</summary>
    public void Shutdown()
    {
        // v0.9.2：决策/审计日志改为合并写，退出前必须显式落盘，否则最后 1.5 秒的记录丢失
        try { Decisions.Flush(); } catch { }
        try { Audit.Flush(); } catch { }
        try { LeaseApi.Dispose(); } catch { }
        try { GpuMonitor.Dispose(); } catch { }
        try { GpuMemory.Dispose(); } catch { }
        try { Engine.Stop(); } catch { }
    }

    public void StartEngine()
    {
        Engine.Start(ct => RefreshAsync(ct));
        // 显存采样：独立 30 秒节奏（窗口最小化也照采）；配置改动在 Tick 内实时读取
        GpuMonitor.Start(ConfigStore.Config.GpuSampleIntervalSeconds,
            () => ConfigStore.Config.GpuSamplingEnabled && ConfigStore.Config.GpuSampleIntervalSeconds > 0,
            () => _lastProcs,
            () => Engine.LastGpuPercent,
            () =>
            {
                var s = SysMem;
                return s.TotalCommitLimitBytes > 0
                    ? (s.TotalCommitLimitBytes - s.TotalCommittedBytes) * 100.0 / s.TotalCommitLimitBytes
                    : null;
            });
        RestartLeaseApi();   // v0.7.0：租约 API 监听（127.0.0.1 + 令牌）
    }

    /// <summary>One refresh cycle. Runs enumeration/classification on a background thread,
    /// marshals only the collection updates to the UI thread. Concurrent calls coalesce.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false)) return; // a refresh is already running
        try
        {
            RefreshStatus(UiStrings.Get("Process_Enumerating"));
            IsBusy = true;

            long enumMs = 0, classifyMs = 0, gpuMs = 0, lookupMs = 0;
            long prepMs = 0, detectMs = 0, otherMs = 0;
            var data = await Task.Run(() =>
            {
                // v0.26.0：枚举之前的 5 个动作此前完全不计时（含 SCM 服务枚举，最可疑的一段）
                var swP = System.Diagnostics.Stopwatch.StartNew();
                Engine.SampleForeground();
                var audio = AudioService.GetActiveAudioPids();
                var suspended = Cleanup.Suspended.SuspendedPids();
                var services = ServiceService.EnumerateServices();
                var svcByPid = ServiceService.MapByPid(services);
                prepMs = swP.ElapsedMilliseconds;

                var swE = System.Diagnostics.Stopwatch.StartNew();
                var procs = ProcessService.EnumerateProcesses();
                enumMs = swE.ElapsedMilliseconds;

                var swC = System.Diagnostics.Stopwatch.StartNew();
                var protectedNames = new HashSet<string>(ConfigStore.Config.ProtectedNames, StringComparer.OrdinalIgnoreCase);
                foreach (var p in procs)
                {
                    Classification.Classify(p, svcByPid, protectedNames);
                    // a mode KeepPattern pins the process from auto-actions, not from manual ones
                    p.ModeKeep = ModeManager.ShouldKeep(p);
                    WorkloadClassifier.Apply(p, _workloadRules, _workloadLookup); // v0.4.0：用户标记 > 强类别 > 外置名单（展示/排序用，不构成处置依据）
                }
                classifyMs = swC.ElapsedMilliseconds;

                var swL = System.Diagnostics.Stopwatch.StartNew();
                LookupUnknowns(procs, ct);
                lookupMs = swL.ElapsedMilliseconds;          // v0.22.0：非阻塞后应接近 0

                var swO = System.Diagnostics.Stopwatch.StartNew();
                MaybeSuggestPromotions();
                otherMs += swO.ElapsedMilliseconds;

                var mem = ProcessService.GetSystemMemory();
                var swG = System.Diagnostics.Stopwatch.StartNew();
                var gpuMem = GpuMemory.Sample();          // 显存：逐进程 + 适配器（PDH，独立于 UI 可见性）
                gpuMs = swG.ElapsedMilliseconds;          // v0.20.0：慢 tick 常见元凶单独计量

                // v0.26.0：逐进程填显存字段 + 未使用判定，此前是纯盲区（500+ 进程逐个判定）
                var swD = System.Diagnostics.Stopwatch.StartNew();
                ApplyGpuMemory(procs, gpuMem);            // 填 ProcessInfo.Gpu*（不可用则保持 false → UI 显示 "-"）
                var unused = UnusedDetector.Detect(procs, mem, Engine.LastForegroundPid, audio, suspended);
                detectMs = swD.ElapsedMilliseconds;

                var swA = System.Diagnostics.Stopwatch.StartNew();
                var gpu = GpuService.GetAdapters(gpuMem); // 名称/容量 + DXGI 缺失时的 PDH 用量兜底
                otherMs += swA.ElapsedMilliseconds;
                return (procs, mem, unused, gpu, gpuMem);
            }, ct).ConfigureAwait(false);

            _lastProcs = data.procs;
            _lastProcsUtc = DateTime.UtcNow;
            _enumerationCount++;
            var memOut = data.mem;
            var unusedOut = data.unused;
            var gpuOut = data.gpu;
            var gpuMemOut = data.gpuMem;

            var uiActive = UiActive;
            var swU = System.Diagnostics.Stopwatch.StartNew();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (uiActive)
                {
                    UpdateCollections(_lastProcs, memOut, unusedOut);
                    UpdateScenePanel();
                    UpdateTrend(memOut);
                }
                else
                {
                    SysMem = memOut; // tray tooltip still needs fresh numbers
                }
                // GPU 字段与窗口可见性无关：StartMinimizedToTray 下首刷走隐藏分支，
                // 若只在 uiActive 里赋值，托盘模式永远显示"GPU 信息不可用"（2026-10-31 实测）
                _gpu = gpuOut;
                _lastGpuMem = gpuMemOut;
                OnPropertyChanged(nameof(GpuSummaryText));
                OnPropertyChanged(nameof(GpuMemorySummaryText));
                OnPropertyChanged(nameof(SelfCostText));
                OnPropertyChanged(nameof(MemoryLabel));
                RefreshStatus(UiStrings.F("Status_Enumerated",
                    _lastProcs.Count, unusedOut.Count, enumMs, classifyMs));
            });
            // v0.20.0：分段耗时交给引擎落盘（self-*.jsonl），慢 tick 可定位到具体阶段
            Engine.LastTimings = new RefreshTimings(
                EnumMs: enumMs, ClassifyMs: classifyMs, GpuMs: gpuMs, UiMs: swU.ElapsedMilliseconds,
                LookupMs: lookupMs, PrepMs: prepMs, DetectMs: detectMs, OtherMs: otherMs);
            Engine.LastTimingsUtc = DateTime.UtcNow;   // v0.25.0：给"分段过期"判定用

            Engine.AfterRefresh(_lastProcs, memOut);
            LeaseManager.CheckGuard(memOut, DateTime.UtcNow);   // v0.7.0：占用期守护
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = UiStrings.Get("Common_RefreshFailed") + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _refreshGate.Release();
        }
    }

    // ---- 告警触达（v0.1.8）：主页横幅 + 托盘气泡双通道 ----

    public event Action<string, string, bool>? AlertRaised;

    private string _alertBannerTitle = "";
    public string AlertBannerTitle { get => _alertBannerTitle; private set { _alertBannerTitle = value; OnPropertyChanged(); } }
    private string _alertBannerText = "";
    public string AlertBannerText { get => _alertBannerText; private set { _alertBannerText = value; OnPropertyChanged(); } }
    // ---- 场景识别诊断（v0.2.2 影子模式） ----
    private string _sceneSummary = UiStrings.Get("Habit_Waiting");
    public string SceneSummary { get => _sceneSummary; private set { _sceneSummary = value; OnPropertyChanged(); } }
    private string _sceneReason = "";
    public string SceneReason { get => _sceneReason; private set { _sceneReason = value; OnPropertyChanged(); } }
    private double _sceneGame, _sceneAi, _sceneDev;
    public double SceneGame { get => _sceneGame; private set { _sceneGame = value; OnPropertyChanged(); } }
    public double SceneAi { get => _sceneAi; private set { _sceneAi = value; OnPropertyChanged(); } }
    public double SceneDev { get => _sceneDev; private set { _sceneDev = value; OnPropertyChanged(); } }

    private string _habitHintText = UiStrings.Get("Habit_NoHint");
    public string HabitHintText { get => _habitHintText; private set { _habitHintText = value; OnPropertyChanged(); } }

    private bool _hasAlert;
    public bool HasAlert { get => _hasAlert; private set { _hasAlert = value; OnPropertyChanged(); } }

    public void RaiseAlert(string title, string message)
        => SetUi(() => { AlertBannerTitle = title; AlertBannerText = message; HasAlert = true; });

    public void DismissAlert() => SetUi(() => HasAlert = false);

    /// <summary>一键低风险腾挪：全机工作集压缩 + 孤儿残留清扫（不含 L3/L4）。</summary>
    public async Task<string> QuickReliefAsync()
    {
        var trimMsg = await TrimAllWorkingSetsAsync();
        var plan = BuildJunkSweepPlan(orphansOnly: true);
        var sweepMsg = await ExecuteJunkSweepAsync(plan);
        DismissAlert();
        return $"{trimMsg}；{sweepMsg}";
    }

    /// <summary>撤销一条反馈（v0.3.7）：删库 + 从列表移除，下次刷新分类即恢复。</summary>
    public bool RemoveFeedback(FeedbackEntry e)
    {
        if (e == null || !Feedback.Remove(e.Id)) return false;
        FeedbackEntries.Remove(e);
        return true;
    }

    public void AddFeedback(FeedbackEntry e)
    {
        Feedback.Add(e);
        FeedbackEntries.Insert(0, e);
        e.Status = "applied";
        Feedback.UpdateStatus(e.Id, "applied");
    }

    private static string CategoryText(ProcessCategory c) => UiTerms.OfCategory(c);

    private void SetUi(Action assign, [CallerMemberName] string? name = null)
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) { disp.InvokeAsync(() => { assign(); OnPropertyChanged(name); }); return; }
        assign();
        OnPropertyChanged(name);
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return b + " B";
        double v = b;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
