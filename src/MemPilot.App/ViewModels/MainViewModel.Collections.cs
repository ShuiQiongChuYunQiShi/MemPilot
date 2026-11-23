using MemPilot.Models;
using System.Collections.ObjectModel;
using MemPilot.Services;

namespace MemPilot.ViewModels;

public partial class MainViewModel
{
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
            ? UiStrings.F("Process_WorkloadMarked", p.Name, WorkloadText.Of(k))
            : UiStrings.F("Process_WorkloadCleared", p.Name);
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
        CandidateRuleText = UiStrings.F("Cleanup_CandidateRule", cfg.UnusedIdleMinutes, cfg.RequiredIdleObservations);
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
                Status = UiStrings.Get("Data_QualityWarning");
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
                DecisionEntries = new ObservableCollection<DecisionEntry>(
                    DecisionLogFilter.Filter(Decisions.Recent(200), ShowNoise));
                break;
            default: // 主页等轻页面：候选计数与首页决策
                RebuildUnused();
                SuspendedProcesses.Clear();
                foreach (var s in Cleanup.Suspended.LiveRecords()) SuspendedProcesses.Add(s);
                HomeDecisions = new ObservableCollection<DecisionEntry>(
                    DecisionLogFilter.Filter(Decisions.Recent(5), ShowNoise));
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
            DecisionEntries = new ObservableCollection<DecisionEntry>(
                DecisionLogFilter.Filter(Decisions.Recent(200), ShowNoise));
            HomeDecisions = new ObservableCollection<DecisionEntry>(
                DecisionLogFilter.Filter(Decisions.Recent(5), ShowNoise));
        });
    }
}
