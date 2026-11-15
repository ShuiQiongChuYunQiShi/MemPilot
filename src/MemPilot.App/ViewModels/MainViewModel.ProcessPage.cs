using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

/// <summary>进程页（v0.3.3 拆自 MainViewModel god-class）：集合、树/用途筛选、线程与未识别复核队列。</summary>
public partial class MainViewModel
{
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

    private bool _treeMode;
    /// <summary>Process page tree mode: children grouped under parents, collapsible.</summary>
    public bool TreeMode
    {
        get => _treeMode;
        set { _treeMode = value; OnPropertyChanged(); ApplyFilter(); }
    }
    private readonly HashSet<int> _collapsedPids = new();

    /// <summary>分类筛选芯片（v0.6.0）：null=全部；game/system/ai/unknown。</summary>
    private string? _categoryChip;
    public string? CategoryChip
    {
        get => _categoryChip;
        set
        {
            if (_categoryChip == value) return;
            _categoryChip = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    /// <summary>用途名单（v0.4.0，来自 %APPDATA%\MemPilot\workload-rules.json；改文件后重启生效）。</summary>
    private readonly WorkloadRules _workloadRules;
    private readonly Func<ProcessInfo, WorkloadKind?> _workloadLookup;

    private string _workloadFilter = "";
    /// <summary>
    /// 用途筛选（"" = 全部）。取值是**稳定 id**（work/entertainment/ai/system/other/unknown，
    /// 见 `PersistedIds.Workload`），不是显示文本——v0.10.0 改，否则加语言会静默重置用户筛选。
    /// </summary>
    public string WorkloadFilter
    {
        get => _workloadFilter;
        set
        {
            var v = value ?? "";
            if (_workloadFilter == v) return;
            _workloadFilter = v;
            OnPropertyChanged();
            ConfigStore.Config.WorkloadFilter = v;
            ConfigStore.Save();
            ApplyFilter();
        }
    }

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

    /// <summary>未识别复核队列（P3.3）：高占用×高频的 Unknown 进程，供人工核实/投票。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ProcessInfo> ReviewQueue { get; } = new();
    private string _reviewQueueHeader = UiStrings.Get("Process_ReviewQueue");
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
            ? UiStrings.Get("Process_ReviewQueueEmpty")
            : UiStrings.F("Process_ReviewQueueCount", ReviewQueue.Count);
    }

    /// <summary>Thread rows are built on demand (thread tab) — one syscall per thread makes
    /// this far too expensive to run inside the refresh loop.</summary>
    private void ApplyFilter()
    {
        IEnumerable<ProcessInfo> src = Processes;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.ToLowerInvariant();
            src = src.Where(p => p.Name.ToLowerInvariant().Contains(q) || p.ExecutablePath.ToLowerInvariant().Contains(q) || p.MainWindowTitle.ToLowerInvariant().Contains(q));
        }
        // 用途筛选先于树构建：树里只留命中的节点，避免"筛了工作却还看到游戏父节点"
        // v0.10.0：筛选值改为稳定 id，显示文本只在 UI 层出现。
        if (!string.IsNullOrEmpty(_workloadFilter))
            src = src.Where(p => WorkloadText.IdOf(p.Workload) == _workloadFilter);
        // v0.6.0 分类芯片：系统·保护 / 游戏 / AI / 未识别（互斥，null=全部）
        switch (_categoryChip)
        {
            case "system":
                src = src.Where(p => p.IsProtected ||
                    p.Category is ProcessCategory.SystemCritical or ProcessCategory.SystemService
                        or ProcessCategory.Driver or ProcessCategory.SecuritySoftware);
                break;
            case "game":
                src = src.Where(p => p.Category is ProcessCategory.Game or ProcessCategory.GameLauncher);
                break;
            case "ai":
                src = src.Where(p => p.Category == ProcessCategory.AiImageVideo);
                break;
            case "unknown":
                src = src.Where(p => p.Category == ProcessCategory.Unknown);
                break;
        }
        if (TreeMode) src = ProcessTreeBuilder.Build(src, _collapsedPids);
        // swap the whole collection instead of Clear+Add: one Reset notification beats
        // 500 Add notifications every refresh (the periodic-jank fix)
        FilteredProcesses = new ObservableCollection<ProcessInfo>(src);
    }
}
