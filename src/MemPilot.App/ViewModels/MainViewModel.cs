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
    private string _aiSessionText = "暂无 AI 会话记录（显存 ≥8GB 持续 90 秒即判定为会话）。";
    public string AiSessionText { get => _aiSessionText; private set { _aiSessionText = value; OnPropertyChanged(); } }

    /// <summary>租约管理器 + 本地 API（v0.7.0）。</summary>
    public LeaseManager LeaseManager { get; }
    public LeaseApiService LeaseApi { get; }
    private string _leaseToken = "";
    public string LeaseToken => _leaseToken;

    private List<GpuAdapterInfo> _gpu = new();
    private GpuMemorySnapshot? _lastGpuMem;
    public string GpuSummaryText => _gpu.Count == 0 ? "GPU 信息不可用" : string.Join("  |  ", _gpu.Select(g => g.Summary));

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
                "pdh-adapter" => "PDH 适配器",
                "pdh-process" => "进程汇总",
                _ => "DXGI"   // v0.10.0：DXGI 已不再提供适配器用量，理论上不会走到这里
            };
            return total > 0
                ? $"显存 {ByteFormat.Human(used)} / {ByteFormat.Human(total)}（{used * 100.0 / total:F0}%）· {src}"
                : $"显存 {ByteFormat.Human(used)} · {src}";
        }
    }

    public string TotalMemoryText => FormatBytes(SysMem.TotalPhysicalBytes);
    public string AvailableMemoryText => FormatBytes(SysMem.AvailablePhysicalBytes);
    public string CommitText => $"{FormatBytes(SysMem.TotalCommittedBytes)} / {FormatBytes(SysMem.TotalCommitLimitBytes)}";
    public string CommitFreeText => FormatBytes(SysMem.TotalCommitLimitBytes - SysMem.TotalCommittedBytes);
    public double UsedPercent => SysMem.TotalPhysicalBytes <= 0 ? 0 : SysMem.MemoryLoadPercent;
    public string UsedPercentText => $"{UsedPercent:F0}%";
    public string UsedDetailText => $"{FormatBytes(SysMem.TotalPhysicalBytes - SysMem.AvailablePhysicalBytes)} 已用 / {TotalMemoryText}";

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
                Status = $"模式已切换，恢复了 {records.Count} 个挂起进程。";
            }
        }
    }
    public string CurrentModeText => ModeManager.Profile.Name;
    public string AppVersionText => "v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?");

    public string ModeDescription => CurrentMode switch
    {
        AppMode.Resident => "安静驻守：只观察和记录，不主动动任何进程。",
        AppMode.Development => "开发模式：保护 IDE/编译器/数据库，游戏和娱乐类进入清理名单。",
        AppMode.Game => "娱乐模式（游戏/直播/影视）：保护游戏、反作弊与播放器，开发工具和 AI 任务进入清理名单。",
        AppMode.AiGeneration => "AI 模式：保护 AI 工具，游戏/浏览器等进入清理名单，允许更积极的腾挪。",
        AppMode.Balanced => "平衡模式：只做最温和的工作集回收，其余交给手动操作。",
        _ => ""
    };

    private string _status = "就绪";
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
    public string BusyText => IsBusy ? "⟳ 刷新中" : "空闲";

    /// <summary>Raised after the UI collections were rebuilt; the view re-applies selection.</summary>
    public event EventHandler? CollectionsRebuilt;
    /// <summary>Raised after an audit write so the view can reload the audit tab.</summary>
    public event EventHandler? AuditChanged;

    private List<ProcessInfo> _lastProcs = new();

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
            () => ConfigStore.Config.LeaseApiPort);
        _workloadLookup = p => Feedback.FindWorkload(p.Name, p.ExecutablePath);
        ModeManager = new ModeManager(config.Config);
        ProgramCatalog = ProgramCatalogService.TryLoad(Path.Combine(ConfigStore.DataDirectory, "programs.json"))
                         ?? new ProgramCatalogService();
        Classification = new ClassificationEngine(feedback, ProgramCatalog);
        UnusedDetector.Config = config.Config;
        OnlineLookup.Config = config.Config;
        Decisions = new DecisionLog(ConfigStore.DataDirectory);
        Usage = new UsageRecorder(ConfigStore.DataDirectory, ConfigStore.Config.UsageRetentionDays);
        Cleanup = new CleanupEngine(ProcessService, audit, ConfigStore.DataDirectory);
        Behaviors = BehaviorLedger.Load(Path.Combine(ConfigStore.DataDirectory, "behavior.json"));
        Engine = new ResidentEngine(config, Cleanup, new NativeMemoryCleanerService(), audit, Decisions, Usage, Behaviors);
        Engine.Alert += (title, message, warning) =>
        {
            if (title.Contains("时段")) SetUi(() => HabitHintText = message);
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
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "开始",
                        Reason = $"{lease.Requester} 申请 {lease.TargetAvailableGb:F1} GB（策略 {lease.Policy}，挂起 {lease.Suspended.Count} 个）；实测达成 {achieved:F1} GB（{lease.Details}）" });
                    RaiseAlert("租约生效", $"已为 {lease.Requester} 腾出 {achieved:F1} GB 可用内存（目标 {peak:F1} GB），至 {lease.ExpiresUtc.ToLocalTime():HH:mm}。");
                    AlertRaised?.Invoke("租约生效", $"{achieved:F1} / {peak:F1} GB", false);
                    break;
                case "归还":
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "归还",
                        Reason = $"{lease.Requester} 主动归还（占用 {(lease.EndUtc - lease.StartUtc)?.TotalMinutes:F0} 分钟，策略 {lease.Policy}）" });
                    RaiseAlert("租约归还", $"{lease.Requester} 已归还租约。");
                    break;
                case "过期":
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "过期",
                        Reason = $"TTL 到期自动结束（占用至 {lease.ExpiresUtc.ToLocalTime():HH:mm}，策略 {lease.Policy}）" });
                    RaiseAlert("租约过期", "TTL 到期，已自动结束并恢复挂起进程。");
                    break;
                case "守护告警":
                    Decisions.Log(new DecisionEntry { Kind = "Lease", Target = lease.Id, Decision = "守护告警",
                        Reason = lease.Details });
                    RaiseAlert("租约被侵蚀", "可用内存跌破了租约授予的下限——占用方可能在超用。");
                    AlertRaised?.Invoke("租约被侵蚀", lease.Details, true);
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

        GpuMonitor.AiSessionChanged += (summary, started) => SetUi(() =>
        {
            Engine.AiSessionActive = started;
            AiSessionActive = started;
            double peakGb = summary.PeakUsedBytes / 1073741824.0;
            double sharedGb = summary.PeakSharedBytes / 1073741824.0;
            if (started)
            {
                AiSessionText = $"AI 会话进行中：显存 {peakGb:F1} GB（主导 {summary.DominantName}）";
                Decisions.Log(new DecisionEntry
                {
                    Kind = "AiSession",
                    Target = summary.DominantName,
                    Decision = "开始",
                    Reason = $"显存 {peakGb:F1} GB（共享 {sharedGb:F1} GB）；判定阈值 8GB、最近 5 个 30 秒样本中 ≥3 个"
                });
                RaiseAlert("AI 生成会话", $"检测到 AI 生成会话：显存 {peakGb:F1} GB（主导 {summary.DominantName}）。只记录，不处置。");
                AlertRaised?.Invoke("AI 生成会话", $"显存 {peakGb:F1} GB（{summary.DominantName}）", false);
            }
            else
            {
                AiSessionText = $"上次 AI 会话：{summary.Duration.TotalMinutes:F0} 分钟，峰值 {peakGb:F1} GB";
                Decisions.Log(new DecisionEntry
                {
                    Kind = "AiSession",
                    Target = summary.DominantName,
                    Decision = "结束",
                    Reason = $"时长 {summary.Duration.TotalMinutes:F0} 分钟；峰值显存 {peakGb:F1} GB（共享 {sharedGb:F1} GB）；" +
                             (summary.PeakCommitFreePct is { } cf ? $"峰值提交余量 {cf:F1}%；" : "") +
                             $"主导进程出现过 {summary.PidCount} 个 PID"
                });
                RaiseAlert("AI 会话结束", $"时长 {summary.Duration.TotalMinutes:F0} 分钟，峰值 {peakGb:F1} GB（{summary.DominantName}）。");
            }
        });
        OnlineLookupService.LoadCache(ConfigStore.DataDirectory);
        ProgramCatalogInfo = ProgramCatalog.Count > 0
            ? $"程序清单：{ProgramCatalog.Count} 条（{ProgramCatalog.BuiltUtc.ToLocalTime():MM-dd HH:mm}）"
            : "程序清单：尚未构建（后台构建中…）";
        if (ProgramCatalog.Count == 0) _ = Task.Run(BuildProgramCatalogInBackground);
        WindowsEventLog.Enabled = config.Config.WriteEventLogAudit;
        SetMode(config.Config.CurrentMode, "startup");
        foreach (var a in audit.Recent(200)) AuditEntries.Add(a);
        foreach (var d in Decisions.Recent(200)) DecisionEntries.Add(d);
        foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        foreach (var f in feedback.All()) FeedbackEntries.Add(f);
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
                    s.RestoreNote = "进程已退出，无需恢复";
                    continue;
                }
                var r = Cleanup.Resume(s.Pid, s.Name);
                s.Restored = r.Success;
                s.RestoreNote = r.Message;
                if (r.Success) restored++; else failed++;
            }
            catch (Exception ex) { failed++; s.RestoreNote = ex.Message; }
        }

        int total = lease.Suspended.Count;
        SetUi(() => Status = $"租约结束，已恢复 {restored} 个挂起进程" + (failed > 0 ? $"（{failed} 个失败）" : "") + "。");
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
            RefreshStatus("正在枚举进程...");
            IsBusy = true;

            long enumMs = 0, classifyMs = 0;
            var data = await Task.Run(() =>
            {
                Engine.SampleForeground();
                var audio = AudioService.GetActiveAudioPids();
                var suspended = Cleanup.Suspended.SuspendedPids();
                var services = ServiceService.EnumerateServices();
                var svcByPid = ServiceService.MapByPid(services);
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

                LookupUnknowns(procs, ct);
                MaybeSuggestPromotions();

                var mem = ProcessService.GetSystemMemory();
                var gpuMem = GpuMemory.Sample();          // 显存：逐进程 + 适配器（PDH，独立于 UI 可见性）
                ApplyGpuMemory(procs, gpuMem);            // 填 ProcessInfo.Gpu*（不可用则保持 false → UI 显示 "-"）
                var unused = UnusedDetector.Detect(procs, mem, Engine.LastForegroundPid, audio, suspended);
                var gpu = GpuService.GetAdapters(gpuMem); // 名称/容量 + DXGI 缺失时的 PDH 用量兜底
                return (procs, mem, unused, gpu, gpuMem);
            }, ct).ConfigureAwait(false);

            _lastProcs = data.procs;
            var memOut = data.mem;
            var unusedOut = data.unused;
            var gpuOut = data.gpu;
            var gpuMemOut = data.gpuMem;

            var uiActive = UiActive;
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
                RefreshStatus($"已枚举 {_lastProcs.Count} 个进程，{unusedOut.Count} 个可清理候选（枚举 {enumMs}ms · 分类 {classifyMs}ms）。");
            });

            Engine.AfterRefresh(_lastProcs, memOut);
            LeaseManager.CheckGuard(memOut, DateTime.UtcNow);   // v0.7.0：占用期守护
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "刷新失败: " + ex.Message;
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
    private string _sceneSummary = "等待第一次判定…";
    public string SceneSummary { get => _sceneSummary; private set { _sceneSummary = value; OnPropertyChanged(); } }
    private string _sceneReason = "";
    public string SceneReason { get => _sceneReason; private set { _sceneReason = value; OnPropertyChanged(); } }
    private double _sceneGame, _sceneAi, _sceneDev;
    public double SceneGame { get => _sceneGame; private set { _sceneGame = value; OnPropertyChanged(); } }
    public double SceneAi { get => _sceneAi; private set { _sceneAi = value; OnPropertyChanged(); } }
    public double SceneDev { get => _sceneDev; private set { _sceneDev = value; OnPropertyChanged(); } }

    private string _habitHintText = "暂无提示——继续积累采样后，这里会显示你的时段画像。";
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

    private static string CategoryText(ProcessCategory c) => c switch
    {
        ProcessCategory.Unknown => "未识别",
        ProcessCategory.SystemCritical => "系统关键",
        ProcessCategory.SystemService => "系统服务",
        ProcessCategory.Driver => "驱动",
        ProcessCategory.ThirdPartyApp => "第三方应用",
        ProcessCategory.Game => "游戏",
        ProcessCategory.GameLauncher => "游戏平台",
        ProcessCategory.AntiCheat => "反作弊",
        ProcessCategory.DevTool => "开发工具",
        ProcessCategory.AiImageVideo => "AI 生图/视频",
        ProcessCategory.ContainerVm => "容器/虚拟机",
        ProcessCategory.BackgroundService => "后台服务",
        ProcessCategory.Bloatware => "垃圾常驻",
        ProcessCategory.SecuritySoftware => "安全软件",
        _ => c.ToString()
    };

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
