using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public ProcessService ProcessService { get; } = new();
    public ServiceService ServiceService { get; } = new();
    public AudioSessionService AudioService { get; } = new();
    public GpuInfoService GpuService { get; } = new();
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
    public ObservableCollection<ProcessInfo> Processes
    {
        get => _processes;
        private set { _processes = value; OnPropertyChanged(); }
    }
    public ObservableCollection<ThreadInfo> Threads { get; } = new();
    private ObservableCollection<ProcessGroup> _groups = new();
    public ObservableCollection<ProcessGroup> Groups
    {
        get => _groups;
        private set { _groups = value; OnPropertyChanged(); }
    }
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

    private List<GpuAdapterInfo> _gpu = new();
    public string GpuSummaryText => _gpu.Count == 0 ? "GPU 信息不可用" : string.Join("  |  ", _gpu.Select(g => g.Summary));

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
    public AppMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (_currentMode == value) return;
            var previous = _currentMode;
            _currentMode = value;
            ConfigStore.Config.CurrentMode = value;
            ConfigStore.Save();
            // 手动/自动模式切换留痕：复盘与习惯学习都依赖这条事件（此前切换无任何记录）
            Audit.Log(new AuditEntry
            {
                Action = "ModeSwitch",
                Target = $"{previous} → {value}",
                Level = "Manual",
                Result = ModeManager.Profile.Name
            });
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentModeText));
            OnPropertyChanged(nameof(ModeDescription));
            if (ConfigStore.Config.ResumeSuspendedOnModeChange)
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
    }
    public string CurrentModeText => ModeManager.Profile.Name;
    public string AppVersionText => "v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?");

    // ---- usage history (历史统计) ----
    public ObservableCollection<ModeUsageRow> ModeUsageItems { get; } = new();
    public ObservableCollection<TopUsageRow> TopUsageItems { get; } = new();
    public ObservableCollection<ActionUsageRow> ActionUsageItems { get; } = new();
    private string _historySummary = "";
    public string HistorySummary { get => _historySummary; private set { _historySummary = value; OnPropertyChanged(); } }

    public sealed class ModeUsageRow
    {
        public string Mode { get; set; } = "";
        public double Hours { get; set; }
        public string Display => $"{Mode}    {Hours:F1} 小时";
    }
    public sealed class TopUsageRow
    {
        public string Name { get; set; } = "";
        public double AvgWsMb { get; set; }
        public double MaxWsMb { get; set; }
        public double AvgCpu { get; set; }
        public double Threads { get; set; }
        public int Seen { get; set; }
    }
    public sealed class ActionUsageRow
    {
        public string Action { get; set; } = "";
        public int Count { get; set; }
    }

    private DateTime _lastHistoryLoadUtc = DateTime.MinValue;

    public void LoadHistory()
    {
        // 页签切换不能读盘算图：数据读取放后台、10s 内复用上次结果，UI 只做点集/行的落位
        if ((DateTime.UtcNow - _lastHistoryLoadUtc).TotalSeconds < 10) return;
        _lastHistoryLoadUtc = DateTime.UtcNow;
        Task.Run(() =>
        {
            var day = Usage.ReadWindow(TimeSpan.FromHours(24));
            var week = Usage.ReadWindow(TimeSpan.FromDays(7));
            var habit = HabitProfile.Build(week);
            var since = DateTime.UtcNow - TimeSpan.FromDays(7);
            var actions = Audit.Recent(2000).Where(a => a.TimestampUtc >= since)
                               .GroupBy(a => a.Action).OrderByDescending(g => g.Count())
                               .Select(g => new ActionUsageRow { Action = g.Key, Count = g.Count() }).ToList();
            return (day, week, habit, actions);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || t.Result.day == null) return;
            var (day, week, habit, actions) = t.Result;
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            HistoryPoints.Clear();
            if (day.Count > 1 && day[0].Total > 0)
            {
                HistoryAreaPoints.Clear();
                for (int i = 0; i < day.Count; i++)
                {
                    double frac = (double)day[i].Avail / day[i].Total; // 0..1 available
                    double x = i * (640.0 / Math.Max(1, day.Count - 1));
                    double y = 150 - frac * 140;
                    HistoryPoints.Add(new System.Windows.Point(x, y));
                    HistoryAreaPoints.Add(new System.Windows.Point(x, y));
                }
                if (HistoryPoints.Count > 1)
                {
                    HistoryAreaPoints.Add(new System.Windows.Point(640, 160));
                    HistoryAreaPoints.Add(new System.Windows.Point(0, 160));
                }
            }
            // mode usage over 7 days
            var week = Usage.ReadWindow(TimeSpan.FromDays(7));
            ModeUsageItems.Clear();
            foreach (var g in week.GroupBy(s => s.Mode).OrderByDescending(g => g.Sum(s => (double)s.Interval)))
            {
                ModeUsageItems.Add(new ModeUsageRow { Mode = g.Key, Hours = g.Sum(s => (double)s.Interval) / 3600.0 });
            }
            // top processes over 24h
            TopUsageItems.Clear();
            var byName = new Dictionary<string, (long sumWs, long maxWs, double sumCpu, long sumThreads, int n)>();
            foreach (var s in day)
                foreach (var tp in s.Top)
                {
                    var cur = byName.TryGetValue(tp.N, out var v) ? v : (0, 0, 0.0, 0L, 0);
                    byName[tp.N] = (cur.sumWs + tp.W, Math.Max(cur.maxWs, tp.W), cur.sumCpu + tp.C, cur.sumThreads + tp.T, cur.n + 1);
                }
            foreach (var kv in byName.OrderByDescending(kv => kv.Value.sumWs).Take(12))
            {
                var v = kv.Value;
                TopUsageItems.Add(new TopUsageRow
                {
                    Name = kv.Key,
                    AvgWsMb = v.sumWs / v.n / 1048576.0,
                    MaxWsMb = v.maxWs / 1048576.0,
                    AvgCpu = Math.Round(v.sumCpu / v.n, 1),
                    Threads = Math.Round((double)v.sumThreads / v.n, 0),
                    Seen = v.n
                });
            }
            // action counts over 7 days from the audit trail (read off-thread in LoadHistory)
            ActionUsageItems.Clear();
            foreach (var a in actions) ActionUsageItems.Add(a);
            int maxGapMin = 0;
            for (int i = 1; i < day.Count; i++)
                maxGapMin = Math.Max(maxGapMin, (int)(day[i].T - day[i - 1].T).TotalMinutes);
            HistorySummary = (day.Count == 0
                ? "（还没有采样数据——工具运行 5 分钟后会出现第一个采样点）"
                : $"24 小时 {day.Count} 个采样 · 最早 {day[0].T.ToLocalTime():HH:mm} · 进程数 {day[0].Procs} → {day[^1].Procs}")
                + (maxGapMin >= 30 ? $" · 最长 {maxGapMin} 分钟未运行（睡眠/退出）" : "")
                + (habit.Hours.Count > 0 ? " ｜ " + habit.Describe() : "");
            });
        });
    }

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

    public System.Windows.Media.PointCollection MemoryTrendPoints { get; } = new();
    public System.Windows.Media.PointCollection MemoryTrendAreaPoints { get; } = new();
    public System.Windows.Media.PointCollection HistoryPoints { get; } = new();
    public System.Windows.Media.PointCollection HistoryAreaPoints { get; } = new();
    private readonly List<(DateTime t, long avail)> _memSamples = new();

    private string _searchText = "";
    public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); ApplyFilter(); } }

    private bool _treeMode;
    /// <summary>Process page tree mode: children grouped under parents, collapsible.</summary>
    public bool TreeMode
    {
        get => _treeMode;
        set { _treeMode = value; OnPropertyChanged(); ApplyFilter(); }
    }
    private readonly HashSet<int> _collapsedPids = new();

    public void ToggleCollapse(int pid)
    {
        if (!_collapsedPids.Remove(pid)) _collapsedPids.Add(pid);
        ApplyFilter();
    }

    private ObservableCollection<ProcessInfo> _filteredProcesses = new();
    public ObservableCollection<ProcessInfo> FilteredProcesses
    {
        get => _filteredProcesses;
        private set { _filteredProcesses = value; OnPropertyChanged(); }
    }

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
            _currentMode = e.Mode;
            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CurrentModeText));
            ReloadDecisions();
        };
        OnlineLookupService.LoadCache(ConfigStore.DataDirectory);
        ProgramCatalogInfo = ProgramCatalog.Count > 0
            ? $"程序清单：{ProgramCatalog.Count} 条（{ProgramCatalog.BuiltUtc.ToLocalTime():MM-dd HH:mm}）"
            : "程序清单：尚未构建（后台构建中…）";
        if (ProgramCatalog.Count == 0) _ = Task.Run(BuildProgramCatalogInBackground);
        WindowsEventLog.Enabled = config.Config.WriteEventLogAudit;
        CurrentMode = config.Config.CurrentMode;
        foreach (var a in audit.Recent(200)) AuditEntries.Add(a);
        foreach (var d in Decisions.Recent(200)) DecisionEntries.Add(d);
        foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        foreach (var f in feedback.All()) FeedbackEntries.Add(f);
    }

    public void StartEngine() => Engine.Start(ct => RefreshAsync(ct));

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
                    WorkloadClassifier.Apply(p); // v0.3.1 用途维度（展示/排序用，不构成处置依据）
                }
                classifyMs = swC.ElapsedMilliseconds;

                LookupUnknowns(procs, ct);
                MaybeSuggestPromotions();

                var mem = ProcessService.GetSystemMemory();
                var unused = UnusedDetector.Detect(procs, mem, Engine.LastForegroundPid, audio, suspended);
                var gpu = GpuService.GetAdapters();
                return (procs, mem, unused, gpu);
            }, ct).ConfigureAwait(false);

            _lastProcs = data.procs;
            var memOut = data.mem;
            var unusedOut = data.unused;
            var gpuOut = data.gpu;

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
                OnPropertyChanged(nameof(GpuSummaryText));
                Status = $"已枚举 {_lastProcs.Count} 个进程，{unusedOut.Count} 个可清理候选（枚举 {enumMs}ms · 分类 {classifyMs}ms）。";
            });

            Engine.AfterRefresh(_lastProcs, memOut);
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
        // 之前每 10s 全量重建进程列表(17列×~550行) + 可清理候选，在内存压力下 UI 线程被拖到秒级。
        if ((DateTime.UtcNow - _lastUiRebuildUtc).TotalSeconds < 4) return;
        RebuildActiveTab();
    }

    private void RebuildActiveTab()
    {
        _lastUiRebuildUtc = DateTime.UtcNow;
        switch (ActiveTabKey)
        {
            case "📋": // 进程页：唯一需要全量排序+分组+过滤的页面
                Processes = new ObservableCollection<ProcessInfo>(_lastProcs.OrderByDescending(x => x.Memory.WorkingSetBytes));
                var grouped = _lastProcs.GroupBy(p => p.Category)
                    .Select(g => new ProcessGroup { Key = g.Key.ToString(), DisplayName = CategoryText(g.Key), Category = g.Key, Processes = g.ToList() })
                    .OrderByDescending(g => g.TotalWorkingSetBytes);
                Groups = new ObservableCollection<ProcessGroup>(grouped);
                ApplyFilter();
                break;
            case "🧹":
                RebuildUnused();
                break;
            case "⏸":
                SuspendedProcesses.Clear();
                foreach (var s in Cleanup.Suspended.LiveRecords()) SuspendedProcesses.Add(s);
                DisposalRecords.Clear();
                foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
                break;
            case "🧠":
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

    // ---- 程序清单（v0.3.0）----

    public ProgramCatalogService ProgramCatalog { get; private set; }

    private string _programCatalogInfo = "";
    public string ProgramCatalogInfo
    {
        get => _programCatalogInfo;
        private set { _programCatalogInfo = value; OnPropertyChanged(); }
    }

    private void BuildProgramCatalogInBackground()
    {
        try
        {
            var built = ProgramCatalogService.Build(msg => SetUi(() => Status = msg));
            built.Save(ConfigStore.DataDirectory);
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ProgramCatalog = built;
                Classification.Catalog = built;
                ProgramCatalogInfo = $"程序清单：{built.Count} 条（{built.BuiltUtc.ToLocalTime():MM-dd HH:mm}）";
            });
        }
        catch { }
    }

    /// <summary>手动重建程序清单（设置页按钮）：注册表 + 开始菜单 + 安装根。</summary>
    public async Task<string> RebuildProgramCatalogAsync()
    {
        Status = "正在重建程序清单（注册表 + 开始菜单 + 安装根）…";
        var built = await Task.Run(() => ProgramCatalogService.Build(msg => SetUi(() => Status = msg))).ConfigureAwait(true);
        built.Save(ConfigStore.DataDirectory);
        ProgramCatalog = built;
        Classification.Catalog = built;
        ProgramCatalogInfo = $"程序清单：{built.Count} 条（{built.BuiltUtc.ToLocalTime():MM-dd HH:mm}）";
        Status = $"程序清单已重建：{built.Count} 条（目录索引 {built.DirectoryIndexCount}）";
        return Status;
    }

    private void RebuildUnused()
    {
        UnusedCandidates.Clear();
        // 垃圾常驻置顶（Bloatware 在候选里优先展示），其余按可回收量降序；并附上驻留分
        foreach (var u in _lastUnused.OrderByDescending(x => x.Process.Category == ProcessCategory.Bloatware)
                                     .ThenByDescending(x => x.EstimatedReclaimableBytes))
        {
            u.JunkScore = Behaviors.JunkScore(u.Process.Name);
            UnusedCandidates.Add(u);
        }
        RebuildReviewQueue();
    }

    /// <summary>未识别复核队列（P3.3）：高占用×高频的 Unknown 进程，供人工核实/投票。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProcessInfo> ReviewQueue { get; } = new();
    private string _reviewQueueHeader = "未识别复核队列";
    public string ReviewQueueHeader { get => _reviewQueueHeader; private set { _reviewQueueHeader = value; OnPropertyChanged(); } }

    private void RebuildReviewQueue()
    {
        ReviewQueue.Clear();
        foreach (var p in _lastProcs.Where(p => p.Category == ProcessCategory.Unknown && !p.AccessDenied)
                                    .OrderByDescending(p => p.Memory.WorkingSetBytes)
                                    .ThenByDescending(p => Behaviors.JunkScore(p.Name))
                                    .Take(15))
            ReviewQueue.Add(p);
        ReviewQueueHeader = ReviewQueue.Count == 0
            ? "未识别复核队列（空）"
            : $"未识别复核队列（{ReviewQueue.Count} 个 · 高占用未识别进程，人工核实后可收录为规则）";
    }

    /// <summary>Thread rows are built on demand (thread tab) — one syscall per thread makes
    /// this far too expensive to run inside the refresh loop.</summary>
    public void RebuildThreads()
    {
        var snapshot = _lastProcs;
        Task.Run(() => ProcessService.EnumerateAllThreads(snapshot))
            .ContinueWith(t =>
            {
                var list = t.Exception == null ? t.Result : new List<ThreadInfo>();
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Threads.Clear();
                    foreach (var th in list) Threads.Add(th);
                });
            });
    }

    private void ApplyFilter()
    {
        IEnumerable<ProcessInfo> src = Processes;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.ToLowerInvariant();
            src = src.Where(p => p.Name.ToLowerInvariant().Contains(q) || p.ExecutablePath.ToLowerInvariant().Contains(q) || p.MainWindowTitle.ToLowerInvariant().Contains(q));
        }
        if (TreeMode) src = ProcessTreeBuilder.Build(src, _collapsedPids);
        // swap the whole collection instead of Clear+Add: one Reset notification beats
        // 500 Add notifications every refresh (the periodic-jank fix)
        FilteredProcesses = new ObservableCollection<ProcessInfo>(src);
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

    public async Task PerformCleanupAsync(UnusedCandidate cand, CleanupActionLevel level)
    {
        // graceful close may wait seconds for exit — never block the UI thread
        await Task.Run(() => Cleanup.Execute(cand.Process, level, ConfigStore.Config.GracefulCloseTimeoutSeconds, escalateToKill: false)).ConfigureAwait(true);
        ReloadAudit();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            DisposalRecords.Clear();
            foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        });
        await Task.Delay(600).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
    }

    /// <summary>一键压缩全部可触及进程的工作集（只压缩、不结束任何进程）。</summary>
    public async Task<string> TrimAllWorkingSetsAsync()
    {
        var procs = _lastProcs.ToList();
        var r = await Task.Run(() => Cleanup.TrimWorkingSets(procs)).ConfigureAwait(true);
        ReloadAudit();
        await Task.Delay(400).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
        var msg = $"已压缩 {r.Trimmed} 个进程的工作集（{r.Failed} 个失败），系统可用内存 +{r.FreedBytes / 1048576.0:F0} MB";
        Status = msg;
        return msg;
    }

    /// <summary>垃圾驻守清扫计划（Bloatware + 宿主已退出的孤儿残留，硬安全边界见 JunkSweeper）。</summary>
    public List<JunkSweepItem> BuildJunkSweepPlan(bool orphansOnly = false)
    {
        var procs = _lastProcs.ToList();
        var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
        return JunkSweeper.BuildPlan(procs, Engine.LastForegroundPid, audio,
            ConfigStore.Config.Whitelist, ConfigStore.Config.MinProcessAgeSeconds, orphansOnly);
    }

    /// <summary>执行垃圾驻守清扫计划，返回摘要。</summary>
    public async Task<string> ExecuteJunkSweepAsync(List<JunkSweepItem> plan)
    {
        int kills = 0, trims = 0, failed = 0;
        if (plan.Count > 0)
        {
            await Task.Run(() =>
            {
                foreach (var item in plan)
                {
                    var r = Cleanup.Execute(item.Process, item.Action, ConfigStore.Config.GracefulCloseTimeoutSeconds);
                    if (!r.Success) failed++;
                    else if (item.Action == CleanupActionLevel.L4ForceKill) kills++;
                    else trims++;
                }
            }).ConfigureAwait(true);
        }
        ReloadAudit();
        await Task.Delay(400).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
        var summary = plan.Count == 0
            ? "没有发现可清扫的垃圾驻守进程"
            : $"垃圾驻守清扫：结束 {kills} 个，回收工作集 {trims} 个，失败 {failed} 个";
        Status = summary;
        return summary;
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

    /// <summary>批量处置候选：levelOverride 为 null 时按各自建议级别执行。</summary>
    public async Task<(int done, int failed, string summary)> BatchCleanupAsync(
        IReadOnlyList<UnusedCandidate> cands, CleanupActionLevel? levelOverride = null)
    {
        int done = 0, failed = 0;
        await Task.Run(() =>
        {
            foreach (var c in cands)
            {
                var level = levelOverride ?? c.SuggestedLevel;
                var r = Cleanup.Execute(c.Process, level, ConfigStore.Config.GracefulCloseTimeoutSeconds, escalateToKill: false);
                if (r.Success) done++; else failed++;
            }
        }).ConfigureAwait(true);
        ReloadAudit();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            DisposalRecords.Clear();
            foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        });
        await Task.Delay(600).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
        var summary = $"批量处置完成：成功 {done} 个，失败 {failed} 个";
        Status = summary;
        return (done, failed, summary);
    }

    public void ResumeSuspended(SuspendRecord rec)
    {
        Cleanup.Resume(rec.Pid, rec.Name);
        ReloadAudit();
        SuspendedProcesses.Remove(rec);
    }

    public void ResumeAllSuspended()
    {
        foreach (var rec in Cleanup.Suspended.LiveRecords())
            Cleanup.Resume(rec.Pid, rec.Name);
        ReloadAudit();
        SuspendedProcesses.Clear();
    }

    /// <summary>Relaunch a previously closed/killed exe (the closest thing to an L4 undo).</summary>
    public bool RestartDisposal(DisposalRecord rec)
    {
        try
        {
            if (string.IsNullOrEmpty(rec.ExePath) || !File.Exists(rec.ExePath)) return false;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = rec.ExePath,
                WorkingDirectory = Path.GetDirectoryName(rec.ExePath) ?? "",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch { return false; }
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

    private void UpdateTrend(SystemMemoryInfo mem)
    {
        _memSamples.Add((DateTime.UtcNow, mem.AvailablePhysicalBytes));
        if (_memSamples.Count > 60) _memSamples.RemoveAt(0);
        MemoryTrendPoints.Clear();
        if (_memSamples.Count > 1)
        {
            long maxV = _memSamples.Max(s => s.avail);
            long minV = _memSamples.Min(s => s.avail);
            double range = Math.Max(1, maxV - minV);
            MemoryTrendAreaPoints.Clear();
            for (int i = 0; i < _memSamples.Count; i++)
            {
                // canvas on the home page is 260x64 — scale to it, or the line gets clipped
                double x = i * (260.0 / Math.Max(1, _memSamples.Count - 1));
                double y = 60 - (_memSamples[i].avail - minV) / range * 54;
                MemoryTrendPoints.Add(new System.Windows.Point(x, y));
                MemoryTrendAreaPoints.Add(new System.Windows.Point(x, y));
            }
            if (MemoryTrendPoints.Count > 1)
            {
                MemoryTrendAreaPoints.Add(new System.Windows.Point(260, 64));
                MemoryTrendAreaPoints.Add(new System.Windows.Point(0, 64));
            }
        }
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
