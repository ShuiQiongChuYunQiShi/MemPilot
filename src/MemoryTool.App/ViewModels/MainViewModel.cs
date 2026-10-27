using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MemoryTool.Models;
using MemoryTool.Services;

namespace MemoryTool.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public ProcessService ProcessService { get; } = new();
    public ServiceService ServiceService { get; } = new();
    public ClassificationEngine Classification { get; }
    public UnusedDetector UnusedDetector { get; } = new();
    public OnlineLookupService OnlineLookup { get; } = new();
    public ModeManager ModeManager { get; }
    public ConfigStore ConfigStore { get; }
    public AuditLogger Audit { get; }
    public FeedbackStore Feedback { get; }

    public ObservableCollection<ProcessInfo> Processes { get; } = new();
    public ObservableCollection<ThreadInfo> Threads { get; } = new();
    public ObservableCollection<ProcessGroup> Groups { get; } = new();
    public ObservableCollection<UnusedCandidate> UnusedCandidates { get; } = new();
    public ObservableCollection<AuditEntry> AuditEntries { get; } = new();
    public ObservableCollection<FeedbackEntry> FeedbackEntries { get; } = new();

    private SystemMemoryInfo _sysMem = new();
    public SystemMemoryInfo SysMem { get => _sysMem; set { _sysMem = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalMemoryText)); OnPropertyChanged(nameof(AvailableMemoryText)); OnPropertyChanged(nameof(CommitText)); } }

    public string TotalMemoryText => FormatBytes(SysMem.TotalPhysicalBytes);
    public string AvailableMemoryText => FormatBytes(SysMem.AvailablePhysicalBytes);
    public string CommitText => $"{FormatBytes(SysMem.TotalCommittedBytes)} / {FormatBytes(SysMem.TotalCommitLimitBytes)}";

    private AppMode _currentMode = AppMode.Resident;
    public AppMode CurrentMode { get => _currentMode; set { _currentMode = value; ConfigStore.Config.CurrentMode = value; ConfigStore.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(CurrentModeText)); } }
    public string CurrentModeText => ModeManager.Profile.Name;

    private string _status = "灏辩华";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }
    private readonly List<(DateTime t, long avail)> _memSamples = new();
    public System.Windows.Media.PointCollection MemoryTrendPoints { get; } = new();
    private double _progressValue;
    public double ProgressValue { get => _progressValue; set { _progressValue = value; OnPropertyChanged(); } }

    private string _searchText = "";
    public string SearchText { get => _searchText; set { _searchText = value; OnPropertyChanged(); ApplyFilter(); } }

    public ObservableCollection<ProcessInfo> FilteredProcesses { get; } = new();

    public MainViewModel(ConfigStore config, AuditLogger audit, FeedbackStore feedback)
    {
        ConfigStore = config;
        Audit = audit;
        Feedback = feedback;
        ModeManager = new ModeManager(config.Config);
        Classification = new ClassificationEngine(feedback);
        UnusedDetector.Config = config.Config;
        OnlineLookup.Config = config.Config;
        CurrentMode = config.Config.CurrentMode;
        foreach (var a in audit.Recent(200)) AuditEntries.Add(a);
        foreach (var f in feedback.All()) FeedbackEntries.Add(f);
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "姝ｅ湪鏋氫妇杩涚▼...";
        try
        {
            var services = ServiceService.EnumerateServices();
            var svcByPid = ServiceService.MapByPid(services);
            var procs = ProcessService.EnumerateProcesses();

            // classify
            var protectedNames = new HashSet<string>(ConfigStore.Config.ProtectedNames, StringComparer.OrdinalIgnoreCase);
            foreach (var p in procs)
            {
                Classification.Classify(p, svcByPid, protectedNames);
                // mode keep/clean influence protection? not override protected
                if (ModeManager.ShouldKeep(p)) p.IsProtected = p.IsProtected || false; // keep doesn't force protect
            }

            // online lookup for unknown (best-effort, limited concurrency)
            if (ConfigStore.Config.OnlineLookupEnabled)
            {
                var unknowns = procs.Where(p => p.Category == ProcessCategory.Unknown || p.CategoryReason == "unclassified").Take(5).ToList();
                foreach (var p in unknowns)
                {
                    try
                    {
                        var r = await OnlineLookup.LookupAsync(p);
                        Classification.ApplyOnlineResult(p, r);
                    }
                    catch { }
                }
            }

            // unused detection
            SysMem = ProcessService.GetSystemMemory();
            _memSamples.Add((DateTime.UtcNow, SysMem.AvailablePhysicalBytes));
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
            UnusedDetector.Config = ConfigStore.Config;
            var unused = UnusedDetector.Detect(procs, SysMem, CurrentMode);

            // update collections on UI thread
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Processes.Clear();
                foreach (var p in procs.OrderByDescending(x => x.Memory.WorkingSetBytes)) Processes.Add(p);
                Threads.Clear();
                foreach (var p in procs)
                    foreach (var t in p.Threads) Threads.Add(t);
                // groups
                Groups.Clear();
                var grouped = procs.GroupBy(p => p.Category).Select(g => new ProcessGroup { Key = g.Key.ToString(), DisplayName = g.Key.ToString(), Category = g.Key, Processes = g.ToList() }).OrderByDescending(g => g.TotalWorkingSetBytes);
                foreach (var g in grouped) Groups.Add(g);
                // unused
                UnusedCandidates.Clear();
                foreach (var u in unused.OrderByDescending(x => x.EstimatedReclaimableBytes)) UnusedCandidates.Add(u);
                ApplyFilter();
            });

            Status = $"已枚举 {procs.Count} 个进程，{unused.Count} 个可清理候选。";
        }
        catch (Exception ex)
        {
            Status = "刷新失败: " + ex.Message;
        }
        finally { IsBusy = false; }
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

    public async Task PerformCleanupAsync(UnusedCandidate cand, CleanupActionLevel level)
    {
        var p = cand.Process;
        long before = SysMem.AvailablePhysicalBytes;
        string result = "ok";
        try
        {
            switch (level)
            {
                case CleanupActionLevel.L1Trim:
                    ProcessService.TrimWorkingSet(p.Pid);
                    break;
                case CleanupActionLevel.L2Suspend:
                    ProcessService.SuspendProcess(p.Pid, p.Threads.Select(t => t.Tid).ToList());
                    break;
                case CleanupActionLevel.L3SuggestClose:
                    // just mark; no auto
                    break;
                case CleanupActionLevel.L4ForceKill:
                    ProcessService.TerminateProcess(p.Pid);
                    break;
            }
        }
        catch (Exception ex) { result = ex.Message; }
        await Task.Delay(500);
        var afterMem = ProcessService.GetSystemMemory();
        SysMem = afterMem;
        Audit.Log(new AuditEntry
        {
            Action = level.ToString(),
            Target = $"{p.Name} (PID {p.Pid})",
            Level = level.ToString(),
            Result = result,
            MemoryBeforeBytes = before,
            MemoryAfterBytes = afterMem.AvailablePhysicalBytes,
            Details = string.Join("; ", cand.Reasons)
        });
        AuditEntries.Insert(0, Audit.Recent(1).First());
    }

    public void AddFeedback(FeedbackEntry e)
    {
        Feedback.Add(e);
        FeedbackEntries.Insert(0, e);
        e.Status = "applied";
        Feedback.UpdateStatus(e.Id, "applied");
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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
