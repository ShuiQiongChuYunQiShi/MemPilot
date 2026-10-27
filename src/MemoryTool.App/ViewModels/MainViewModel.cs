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
    public ConfigStore ConfigStore { get; }
    public AuditLogger Audit { get; }
    public FeedbackStore Feedback { get; }

    public ObservableCollection<ProcessInfo> Processes { get; } = new();
    public ObservableCollection<ThreadInfo> Threads { get; } = new();
    public ObservableCollection<ProcessGroup> Groups { get; } = new();
    public ObservableCollection<UnusedCandidate> UnusedCandidates { get; } = new();
    public ObservableCollection<AuditEntry> AuditEntries { get; } = new();
    public ObservableCollection<FeedbackEntry> FeedbackEntries { get; } = new();
    public ObservableCollection<SuspendRecord> SuspendedProcesses { get; } = new();
    public ObservableCollection<DecisionEntry> DecisionEntries { get; } = new();
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
        }
    }
    public string CurrentModeText => ModeManager.Profile.Name;
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
    private readonly List<(DateTime t, long avail)> _memSamples = new();

    private string _searchText = "";
    public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); ApplyFilter(); } }

    public ObservableCollection<ProcessInfo> FilteredProcesses { get; } = new();

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
        Cleanup = new CleanupEngine(ProcessService, audit, ConfigStore.DataDirectory);
        Engine = new ResidentEngine(config, Cleanup, new NativeMemoryCleanerService(), audit, Decisions);
        Engine.ModeChanged += (s, e) =>
        {
            _currentMode = e.Mode;
            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CurrentModeText));
            ReloadDecisions();
        };
        OnlineLookupService.LoadCache(ConfigStore.DataDirectory);
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

    private void UpdateCollections(List<ProcessInfo> procs, SystemMemoryInfo mem, List<UnusedCandidate> unused)
    {
        SysMem = mem;
        Processes.Clear();
        foreach (var p in procs.OrderByDescending(x => x.Memory.WorkingSetBytes)) Processes.Add(p);
        Groups.Clear();
        var grouped = procs.GroupBy(p => p.Category)
            .Select(g => new ProcessGroup { Key = g.Key.ToString(), DisplayName = CategoryText(g.Key), Category = g.Key, Processes = g.ToList() })
            .OrderByDescending(g => g.TotalWorkingSetBytes);
        foreach (var g in grouped) Groups.Add(g);
        UnusedCandidates.Clear();
        foreach (var u in unused.OrderByDescending(x => x.EstimatedReclaimableBytes)) UnusedCandidates.Add(u);
        SuspendedProcesses.Clear();
        foreach (var s in Cleanup.Suspended.LiveRecords()) SuspendedProcesses.Add(s);
        DecisionEntries.Clear();
        foreach (var d in Decisions.Recent(200)) DecisionEntries.Add(d);
        HomeDecisions.Clear();
        foreach (var d in Decisions.Recent(5)) HomeDecisions.Add(d);
        var cfg = ConfigStore.Config;
        CandidateRuleText = $"候选规则：无窗口 + 低 CPU/IO + 没在发声 + 不在前台，且持续空闲 ≥ {cfg.UnusedIdleMinutes} 分钟、连续 {cfg.RequiredIdleObservations} 轮观察。工具刚启动时这里为空是正常的——观察正在累积，默认约 {cfg.UnusedIdleMinutes} 分钟后才会出现第一个候选。";
        DisposalRecords.Clear();
        foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        ApplyFilter();
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
        FilteredProcesses.Clear();
        IEnumerable<ProcessInfo> src = Processes;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.ToLowerInvariant();
            src = src.Where(p => p.Name.ToLowerInvariant().Contains(q) || p.ExecutablePath.ToLowerInvariant().Contains(q) || p.MainWindowTitle.ToLowerInvariant().Contains(q));
        }
        foreach (var p in src) FilteredProcesses.Add(p);
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
            for (int i = 0; i < _memSamples.Count; i++)
            {
                // canvas on the home page is 260x64 — scale to it, or the line gets clipped
                double x = i * (260.0 / Math.Max(1, _memSamples.Count - 1));
                double y = 60 - (_memSamples[i].avail - minV) / range * 54;
                MemoryTrendPoints.Add(new System.Windows.Point(x, y));
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
