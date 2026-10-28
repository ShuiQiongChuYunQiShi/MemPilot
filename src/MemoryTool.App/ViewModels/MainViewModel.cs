using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MemoryTool.Models;
using MemoryTool.Services;

namespace MemoryTool.ViewModels;

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
    public ObservableCollection<DecisionEntry> HomeDecisions { get; } = new();

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
            _currentMode = value;
            ConfigStore.Config.CurrentMode = value;
            ConfigStore.Save();
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

    public void LoadHistory()
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // 24h available-memory curve
            var day = Usage.ReadWindow(TimeSpan.FromHours(24));
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
            // action counts over 7 days from the audit trail
            ActionUsageItems.Clear();
            var since = DateTime.UtcNow - TimeSpan.FromDays(7);
            var actions = Audit.Recent(2000).Where(a => a.TimestampUtc >= since)
                               .GroupBy(a => a.Action).OrderByDescending(g => g.Count());
            foreach (var g in actions)
                ActionUsageItems.Add(new ActionUsageRow { Action = g.Key, Count = g.Count() });
            HistorySummary = day.Count == 0
                ? "（还没有采样数据——工具运行 5 分钟后会出现第一个采样点）"
                : $"24 小时 {day.Count} 个采样 · 最早 {day[0].T.ToLocalTime():HH:mm} · 进程数 {day[0].Procs} → {day[^1].Procs}";
        });
    }

    public string ModeDescription => CurrentMode switch
    {
        AppMode.Resident => "安静驻守：只观察和记录，不主动动任何进程。",
        AppMode.Development => "开发模式：保护 IDE/编译器/数据库，游戏和娱乐类进入清理名单。",
        AppMode.Game => "游戏模式：保护游戏和反作弊，开发工具和 AI 任务进入清理名单。",
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
        Classification = new ClassificationEngine(feedback);
        UnusedDetector.Config = config.Config;
        OnlineLookup.Config = config.Config;
        Decisions = new DecisionLog(ConfigStore.DataDirectory);
        Usage = new UsageRecorder(ConfigStore.DataDirectory, ConfigStore.Config.UsageRetentionDays);
        Cleanup = new CleanupEngine(ProcessService, audit, ConfigStore.DataDirectory);
        Engine = new ResidentEngine(config, Cleanup, new NativeMemoryCleanerService(), audit, Decisions, Usage);
        Engine.ModeChanged += (s, e) =>
        {
            _currentMode = e.Mode;
            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CurrentModeText));
            ReloadDecisions();
        };
        OnlineLookupService.LoadCache(ConfigStore.DataDirectory);
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

            var data = await Task.Run(() =>
            {
                Engine.SampleForeground();
                var audio = AudioService.GetActiveAudioPids();
                var suspended = Cleanup.Suspended.SuspendedPids();
                var services = ServiceService.EnumerateServices();
                var svcByPid = ServiceService.MapByPid(services);
                var procs = ProcessService.EnumerateProcesses();

                var protectedNames = new HashSet<string>(ConfigStore.Config.ProtectedNames, StringComparer.OrdinalIgnoreCase);
                foreach (var p in procs)
                {
                    Classification.Classify(p, svcByPid, protectedNames);
                    // a mode KeepPattern pins the process from auto-actions, not from manual ones
                    p.ModeKeep = ModeManager.ShouldKeep(p);
                }

                LookupUnknowns(procs, ct);

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
                    _gpu = gpuOut;
                    OnPropertyChanged(nameof(GpuSummaryText));
                    UpdateTrend(memOut);
                }
                else
                {
                    SysMem = memOut; // tray tooltip still needs fresh numbers
                }
                Status = $"已枚举 {_lastProcs.Count} 个进程，{unusedOut.Count} 个可清理候选。";
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
        try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(15)); } catch { }
    }

    private bool _dataDiagDone;

    private void UpdateCollections(List<ProcessInfo> procs, SystemMemoryInfo mem, List<UnusedCandidate> unused)
    {
        SysMem = mem;
        Processes = new ObservableCollection<ProcessInfo>(procs.OrderByDescending(x => x.Memory.WorkingSetBytes));
        var grouped = procs.GroupBy(p => p.Category)
            .Select(g => new ProcessGroup { Key = g.Key.ToString(), DisplayName = CategoryText(g.Key), Category = g.Key, Processes = g.ToList() })
            .OrderByDescending(g => g.TotalWorkingSetBytes);
        Groups = new ObservableCollection<ProcessGroup>(grouped);
        UnusedCandidates.Clear();
        foreach (var u in unused.OrderByDescending(x => x.EstimatedReclaimableBytes)) UnusedCandidates.Add(u);
        SuspendedProcesses.Clear();
        foreach (var s in Cleanup.Suspended.LiveRecords()) SuspendedProcesses.Add(s);
        DecisionEntries = new ObservableCollection<DecisionEntry>(Decisions.Recent(200));
        HomeDecisions = new ObservableCollection<DecisionEntry>(Decisions.Recent(5));
        var cfg = ConfigStore.Config;
        CandidateRuleText = $"候选规则：无窗口 + 低 CPU/IO + 没在发声 + 不在前台，且持续空闲 ≥ {cfg.UnusedIdleMinutes} 分钟、连续 {cfg.RequiredIdleObservations} 轮观察。工具刚启动时这里为空是正常的——观察正在累积，默认约 {cfg.UnusedIdleMinutes} 分钟后才会出现第一个候选。";
        DisposalRecords.Clear();
        foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        ApplyFilter();
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
        CollectionsRebuilt?.Invoke(this, EventArgs.Empty);
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
            DecisionEntries.Clear();
            foreach (var d in Decisions.Recent(200)) DecisionEntries.Add(d);
            HomeDecisions.Clear();
            foreach (var d in Decisions.Recent(5)) HomeDecisions.Add(d);
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
        var plan = Cleanup.BuildPlan(_lastProcs, mem, profile, req, Engine.LastForegroundPid, audio);
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
