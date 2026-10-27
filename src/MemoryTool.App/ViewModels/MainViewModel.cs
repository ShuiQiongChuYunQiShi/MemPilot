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
        }
    }

    private List<GpuAdapterInfo> _gpu = new();
    public string GpuSummaryText => _gpu.Count == 0 ? "GPU 信息不可用" : string.Join("  |  ", _gpu.Select(g => g.Summary));

    public string TotalMemoryText => FormatBytes(SysMem.TotalPhysicalBytes);
    public string AvailableMemoryText => FormatBytes(SysMem.AvailablePhysicalBytes);
    public string CommitText => $"{FormatBytes(SysMem.TotalCommittedBytes)} / {FormatBytes(SysMem.TotalCommitLimitBytes)}";
    public string CommitFreeText => FormatBytes(SysMem.TotalCommitLimitBytes - SysMem.TotalCommittedBytes);

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
        }
    }
    public string CurrentModeText => ModeManager.Profile.Name;

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

    public MainViewModel(ConfigStore config, AuditLogger audit, FeedbackStore feedback)
    {
        ConfigStore = config;
        Audit = audit;
        Feedback = feedback;
        ModeManager = new ModeManager(config.Config);
        Classification = new ClassificationEngine(feedback);
        UnusedDetector.Config = config.Config;
        OnlineLookup.Config = config.Config;
        Cleanup = new CleanupEngine(ProcessService, audit, ConfigStore.DataDirectory);
        Engine = new ResidentEngine(config, Cleanup, new NativeMemoryCleanerService(), audit);
        Engine.ModeChanged += (s, m) => { _currentMode = m; OnPropertyChanged(nameof(CurrentMode)); OnPropertyChanged(nameof(CurrentModeText)); };
        CurrentMode = config.Config.CurrentMode;
        foreach (var a in audit.Recent(200)) AuditEntries.Add(a);
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

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateCollections(_lastProcs, memOut, unusedOut);
                _gpu = gpuOut;
                OnPropertyChanged(nameof(GpuSummaryText));
                UpdateTrend(memOut);
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
        ApplyFilter();
        CollectionsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Thread rows are rebuilt lazily — only when the thread tab is opened.</summary>
    public void RebuildThreads()
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Threads.Clear();
            foreach (var p in _lastProcs)
                foreach (var t in p.Threads) Threads.Add(t);
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

    public async Task PerformCleanupAsync(UnusedCandidate cand, CleanupActionLevel level)
    {
        // graceful close may wait seconds for exit — never block the UI thread
        await Task.Run(() => Cleanup.Execute(cand.Process, level, ConfigStore.Config.GracefulCloseTimeoutSeconds, escalateToKill: false)).ConfigureAwait(true);
        ReloadAudit();
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
                double x = i * (300.0 / Math.Max(1, _memSamples.Count - 1));
                double y = 80 - (_memSamples[i].avail - minV) / range * 70;
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
