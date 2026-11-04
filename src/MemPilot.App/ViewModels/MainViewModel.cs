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
                _ => "DXGI"
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
    public string Status { get => _status; set => SetUi(() => _status = value); }

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
        Engine.CommitReliefL3Suggested += (candidates, reclaimBytes) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (candidates.Count == 0) return;
                var names = string.Join("、", candidates.Take(6).Select(p => $"{p.Name}(PID {p.Pid})"));
                var more = candidates.Count > 6 ? $" 等 {candidates.Count} 个" : "";
                var msg = "提交内存告急（余量 <5%）。\n\n可通过优雅关闭以下闲置后台来真正释放提交内存：\n" + names + more +
                          "\n\n预计释放 " + (reclaimBytes / 1048576.0).ToString("F0") + " MB。它们会收到正常关闭请求，不会被强制结束。\n继续？";
                if (System.Windows.MessageBox.Show(msg, "提交内存告急", System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
                {
                    Status = "已忽略本次提交兜底（不会关闭任何进程；压缩只能释放物理内存，降不了提交）。";
                    return;
                }
                _ = Task.Run(async () =>
                {
                    int ok = 0;
                    foreach (var p in candidates)
                    {
                        var r = Cleanup.Execute(p, CleanupActionLevel.L3GracefulClose,
                            ConfigStore.Config.GracefulCloseTimeoutSeconds);
                        if (r.Success) ok++;
                        Decisions.Log(new DecisionEntry
                        {
                            Kind = "CommitRelief",
                            Target = $"{p.Name} (PID {p.Pid})",
                            Decision = r.Success ? "L3 已关闭" : "L3 失败",
                            Reason = $"用户确认后释放提交；结果: {r.Message}"
                        });
                    }
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        Status = $"提交兜底：已优雅关闭 {ok}/{candidates.Count} 个闲置后台。";
                        await RefreshAsync();
                    });
                });
            });

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
    private (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended) ExecuteLeaseRelief(
        LeaseRequest req, LeasePolicy policy)
    {
        var before = ProcessService.GetSystemMemory();
        var audio = new HashSet<int>(_lastProcs.Where(p => p.AudioActive).Select(p => p.Pid));
        var fg = Engine.LastForegroundPid;

        // 一级：清低优先级备用列表（零风险）
        _memCleanerForLease.CleanAreas(new List<MemoryArea> { MemoryArea.StandbyListLowPriority });

        // 二级：安全子集压缩
        var safe = TrimSelection.BuildSafeTrimSet(_lastProcs, fg, audio);
        var trim = Cleanup.TrimWorkingSets(safe);
        var detail = $"压缩安全子集 {trim.Trimmed} 个（排除 {_lastProcs.Count - safe.Count} 个）";

        var suspended = new List<LeaseSuspendedProcess>();
        if (policy == LeasePolicy.Hard)
        {
            // 三级：还不够目标额度就挂起安全子集里"足够闲"的进程（可恢复，归还时逐个恢复）
            double availGb = ProcessService.GetSystemMemory().AvailablePhysicalBytes / 1073741824.0;
            double needGb = req.TargetAvailableGb - availGb;
            if (needGb > 0)
            {
                long needBytes = (long)(needGb * 1073741824.0);
                var candidates = LeaseHardPlanner.PickSuspendCandidates(_lastProcs, fg, audio, DateTime.Now);
                var picked = LeaseHardPlanner.TakeForShortfall(candidates, needBytes);
                int ok = 0;
                foreach (var p in picked)
                {
                    try
                    {
                        Cleanup.Execute(p, CleanupActionLevel.L2Suspend, 0);
                        suspended.Add(new LeaseSuspendedProcess
                        {
                            Pid = p.Pid, StartTime = p.StartTime, Name = p.Name,
                            PrivateBytes = p.Memory.PrivateBytes
                        });
                        ok++;
                    }
                    catch (Exception ex) { Diag.Record("lease:suspend", ex); }
                }
                detail += $"；挂起 {ok} 个闲置进程（可恢复）";
            }
        }

        var after = ProcessService.GetSystemMemory();
        detail = $"{detail}；可用 {before.AvailablePhysicalBytes / 1073741824.0:F1} → " +
                 $"{after.AvailablePhysicalBytes / 1073741824.0:F1} GB";
        return (new LeaseReliefResult(
            before.AvailablePhysicalBytes / 1073741824.0,
            after.AvailablePhysicalBytes / 1073741824.0,
            detail), suspended);
    }

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

    private readonly NativeMemoryCleanerService _memCleanerForLease = new();

    /// <summary>重新生成租约令牌（设置页按钮）。</summary>
    public void RegenerateLeaseToken()
    {
        _leaseToken = LeaseTokenStore.Regenerate(ConfigStore.DataDirectory);
        OnPropertyChanged(nameof(LeaseToken));
    }

    /// <summary>租约 API 开关变更/端口变更后重启监听（设置保存时调用）。</summary>
    public void RestartLeaseApi()
    {
        LeaseApi.Stop();
        if (ConfigStore.Config.LeaseApiEnabled)
            LeaseApi.Start();
    }

    /// <summary>退出前释放资源（v0.3.8，棱镜 backend/resource-lifecycle）：
    /// 显存采样定时器 + PDH 查询句柄 + 引擎定时器。进程退出虽会回收，但 Dispose 存在就该有人调。</summary>
    public void Shutdown()
    {
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
            Status = "正在枚举进程...";
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
                Status = $"已枚举 {_lastProcs.Count} 个进程，{unusedOut.Count} 个可清理候选（枚举 {enumMs}ms · 分类 {classifyMs}ms）。";
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

    /// <summary>
    /// 右键「标记用途」（v0.4.0）：写入反馈库（同一目标替换），下一轮刷新即生效。
    /// kind=null 表示清除标记、回到规则判定。用途仍然只影响展示/筛选，不构成处置依据。
    /// </summary>
    public void MarkWorkload(ProcessInfo p, WorkloadKind? kind)
    {
        if (p == null) return;
        Feedback.SetWorkload(p.Name, p.ExecutablePath, kind);
        FeedbackEntries.Clear();
        foreach (var f in Feedback.All()) FeedbackEntries.Add(f);
        Status = kind is { } k
            ? $"已标记 {p.Name} 用途为「{WorkloadText.Of(k)}」（下次刷新生效，可在反馈页撤销）。"
            : $"已清除 {p.Name} 的用途标记，回到规则判定。";
        _ = RefreshAsync();
    }

    /// <summary>把显存快照填进进程（进程页「显存」列用）。采集不可用时全部保持 false，UI 显示 "-"。</summary>
    private static void ApplyGpuMemory(List<ProcessInfo> procs, GpuMemorySnapshot snap)
    {
        if (!snap.ProcessDedicatedAvailable) return;
        foreach (var p in procs)
        {
            p.GpuMemoryAvailable = true;
            p.GpuDedicatedMemoryBytes = snap.ProcessDedicated.GetValueOrDefault(p.Pid);
            p.GpuSharedMemoryBytes = snap.ProcessShared.GetValueOrDefault(p.Pid);
        }
    }

    private DateTime _lastPromotionScanUtc = DateTime.MinValue;
    private readonly HashSet<string> _promotionSuggested = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>反馈驱动晋升（P3.5）：近 30 天你手动结束 ≥3 次、且当前不在垃圾规则里的进程，
    /// 在决策日志里提议收录为规则。永不自动扩大规则库，只给建议。</summary>
    private void MaybeSuggestPromotions()
    {
        if ((DateTime.UtcNow - _lastPromotionScanUtc).TotalMinutes < 10) return;
        _lastPromotionScanUtc = DateTime.UtcNow;
        try
        {
            var since = DateTime.UtcNow - TimeSpan.FromDays(30);
            var counts = Audit.Recent(2000)
                .Where(a => a.Action == "L4ForceKill" && a.TimestampUtc >= since)
                .Select(a => a.Target.Split(" (PID")[0])
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 3)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in counts)
            {
                if (_promotionSuggested.Contains(kv.Key)) continue;
                _promotionSuggested.Add(kv.Key);
                var p = _lastProcs.FirstOrDefault(x => x.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (p == null || p.Category == ProcessCategory.Bloatware) continue; // 已在垃圾规则里，无需建议
                Decisions.Log(new DecisionEntry
                {
                    Kind = "PromoteRule",
                    Target = kv.Key,
                    Decision = "建议",
                    Reason = $"近 30 天你手动结束了它 {kv.Value} 次（当前分类 {p.Category}）——要把「{kv.Key}」收录为垃圾规则吗？可在反馈页提交，规则库永不自动扩大"
                });
            }
            MaybeRunRuleAudit();
        }
        catch { }
    }

    /// <summary>规则卫生周报（P3.4）：每 7 天一次，写决策日志（Kind=RuleAudit）。只报告不修改规则。</summary>
    private void MaybeRunRuleAudit()
    {
        var recent = Decisions.Recent(300);
        if (recent.Any(d => d.Kind == "RuleAudit" && (DateTime.UtcNow - d.TimestampUtc).TotalDays < 7)) return;
        var report = RuleHygiene.Build(Decisions.Recent(2000), Feedback.All(), Behaviors, DateTime.UtcNow.AddDays(-7));
        Decisions.Log(new DecisionEntry
        {
            Kind = "RuleAudit",
            Target = "-",
            Decision = "周报",
            Reason = report.Text
        });
    }

    private void LookupUnknowns(List<ProcessInfo> procs, CancellationToken ct)
    {
        if (!ConfigStore.Config.OnlineLookupEnabled) return;
        var unknowns = procs.Where(p => p.Category == ProcessCategory.Unknown && !p.AccessDenied).Take(5).ToList();
        if (unknowns.Count == 0) return;
        // bounded parallelism, each with per-process cache so refreshes don't re-query
        using var gate = new SemaphoreSlim(3);
        var tasks = unknowns.Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var r = await OnlineLookup.LookupAsync(p).ConfigureAwait(false);
                Classification.ApplyOnlineResult(p, r);
            }
            catch { }
            finally { gate.Release(); }
        });
        // 未知进程查询必须有界：旧版等 15s 会把刷新管道拖死（实测刷新周期被拉长到远超 10s）
        try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3)); } catch { }
    }

    private bool _dataDiagDone;

    private List<UnusedCandidate> _lastUnused = new();
    private DateTime _lastUiRebuildUtc = DateTime.MinValue;
    /// <summary>当前可见页签（由 MainWindow.TabControl_SelectionChanged 写入）。</summary>
    public string ActiveTabKey { get; set; } = "🏠";

    /// <summary>页签切换时立即重建该页签的集合（不走节流），避免切换后看到旧数据。</summary>
    public void OnTabActivated(string key)
    {
        ActiveTabKey = key;
        if (_lastProcs.Count > 0) RebuildActiveTab();
    }

    private void UpdateScenePanel()
    {
        var d = Engine.LastSceneDecision;
        if (d == null) return;
        SceneGame = Math.Round(d.GameScore);
        SceneAi = Math.Round(d.AiScore);
        SceneDev = Math.Round(d.DevScore);
        SceneSummary = d.Summary;
        SceneReason = d.Reason;
    }

    private void UpdateCollections(List<ProcessInfo> procs, SystemMemoryInfo mem, List<UnusedCandidate> unused)
    {
        SysMem = mem;
        _lastProcs = procs;
        _lastUnused = unused;
        var cfg = ConfigStore.Config;
        CandidateRuleText = $"候选规则：无窗口 + 低 CPU/IO + 没在发声 + 不在前台，且持续空闲 ≥ {cfg.UnusedIdleMinutes} 分钟、连续 {cfg.RequiredIdleObservations} 轮观察。工具刚启动时这里为空是正常的——观察正在累积，默认约 {cfg.UnusedIdleMinutes} 分钟后才会出现第一个候选。";
        // data sanity: if a whole refresh came back without private working sets,
        // the K32 path is broken again — say so loudly instead of failing silently
        if (!_dataDiagDone && procs.Count > 50)
        {
            _dataDiagDone = true;
            if (procs.All(p => p.Memory.PrivateWorkingSetBytes == 0))
            {
                Decisions.Log(new DecisionEntry
                {
                    Kind = "Diag",
                    Target = "-",
                    Decision = "警告",
                    Reason = "本次刷新所有进程的私有工作集均为 0 —— K32 数据通路损坏（历史性问题复现？），预计释放/腾挪规划将不可用"
                });
                Status = "⚠ 数据质量异常：私有工作集全为零，请查看决策日志。";
            }
        }
        // 页签感知重建：只重建当前可见页签的集合；可见页最短 4s 节流（切页签由 OnTabActivated 强制）。
        // 之前每 10s 全量重建进程列表(19列×~550行) + 可清理候选，在内存压力下 UI 线程被拖到秒级。
        // 判断本身在 TabRebuildPolicy（Core 纯函数，有测试守着，防止重构退回「每 tick 全量重建」）。
        if (!TabRebuildPolicy.ShouldRebuild(_lastUiRebuildUtc, DateTime.UtcNow)) return;
        RebuildActiveTab();
    }

    private void RebuildActiveTab()
    {
        _lastUiRebuildUtc = DateTime.UtcNow;
        switch (TabRebuildPolicy.Parse(ActiveTabKey))
        {
            case TabPage.Process: // 进程页：唯一需要全量排序+分组+过滤的页面
                Processes = new ObservableCollection<ProcessInfo>(_lastProcs.OrderByDescending(x => x.Memory.WorkingSetBytes));
                var grouped = _lastProcs.GroupBy(p => p.Category)
                    .Select(g => new ProcessGroup { Key = g.Key.ToString(), DisplayName = CategoryText(g.Key), Category = g.Key, Processes = g.ToList() })
                    .OrderByDescending(g => g.TotalWorkingSetBytes);
                Groups = new ObservableCollection<ProcessGroup>(grouped);
                ApplyFilter();
                break;
            case TabPage.Cleanup:
                RebuildUnused();
                break;
            case TabPage.Suspended:
                SuspendedProcesses.Clear();
                foreach (var s in Cleanup.Suspended.LiveRecords()) SuspendedProcesses.Add(s);
                DisposalRecords.Clear();
                foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
                break;
            case TabPage.Decisions:
                DecisionEntries = new ObservableCollection<DecisionEntry>(Decisions.Recent(200));
                break;
            default: // 主页等轻页面：候选计数与首页决策
                RebuildUnused();
                SuspendedProcesses.Clear();
                foreach (var s in Cleanup.Suspended.LiveRecords()) SuspendedProcesses.Add(s);
                HomeDecisions = new ObservableCollection<DecisionEntry>(Decisions.Recent(5));
                break;
        }
        CollectionsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    public void ReloadAudit()
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            AuditEntries.Clear();
            foreach (var a in Audit.Recent(200)) AuditEntries.Add(a);
            AuditChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void ReloadDecisions()
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            DecisionEntries = new ObservableCollection<DecisionEntry>(Decisions.Recent(200));
            HomeDecisions = new ObservableCollection<DecisionEntry>(Decisions.Recent(5));
        });
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

    // ---- make-room workflow ----

    public async Task<MakeRoomPlan> BuildMakeRoomPlanAsync(MakeRoomRequest req)
    {
        if (_lastProcs.Count == 0) await RefreshAsync().ConfigureAwait(true);
        Engine.SampleForeground();
        var audio = AudioService.GetActiveAudioPids();
        var mem = SysMem.TotalPhysicalBytes == 0 ? ProcessService.GetSystemMemory() : SysMem;
        var profile = ConfigStore.Config.ModeProfiles.TryGetValue(ConfigStore.Config.CurrentMode, out var pr)
            ? pr
            : new ModeProfile { Mode = ConfigStore.Config.CurrentMode, Name = ConfigStore.Config.CurrentMode.ToString() };
        // 历史权重（近 7 天出镜率 × 峰值 P90）：只影响排序，不动资格
        var weights = UsageAnalytics.ComputeProcessWeights(Usage.ReadWindow(TimeSpan.FromDays(7)));
        var plan = Cleanup.BuildPlan(_lastProcs, mem, profile, req, Engine.LastForegroundPid, audio, weights);
        ReloadAudit();
        return plan;
    }

    public async Task<MakeRoomResult> ExecuteMakeRoomAsync(MakeRoomPlan plan, CancellationToken ct)
    {
        var result = await Task.Run(() => Cleanup.ExecutePlanAsync(plan, ConfigStore.Config.GracefulCloseTimeoutSeconds, ct)).ConfigureAwait(false);
        ReloadAudit();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => SysMem = ProcessService.GetSystemMemory());
        return result;
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
