using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

public partial class MainViewModel
{
    private DateTime _lastL3CardUtc = DateTime.MinValue;

    private System.Collections.Generic.IReadOnlyList<ProcessInfo>? _l3Candidates;
    /// <summary>提交告急 L3 确认卡的候选名单（非模态，主页显示）。</summary>
    public System.Collections.Generic.IReadOnlyList<ProcessInfo>? CommitReliefCandidates
    {
        get => _l3Candidates;
        private set => SetUi(() => _l3Candidates = value);
    }
    public long CommitReliefReclaimMb { get; private set; }

    private bool _l3Pending;
    /// <summary>是否显示 L3 确认卡。</summary>
    public bool CommitReliefPending
    {
        get => _l3Pending;
        private set => SetUi(() => _l3Pending = value);
    }

    /// <summary>L3 确认卡上展示的候选名单文案。</summary>
    public string CommitReliefNames
    {
        get
        {
            var c = _l3Candidates;
            if (c == null || c.Count == 0) return "";
            var names = string.Join(UiStrings.Get("Common_ListSeparator"), c.Take(6).Select(p => $"{p.Name}(PID {p.Pid})"));
            return names + (c.Count > 6 ? UiStrings.F("Common_AndMore", c.Count) : "");
        }
    }

    /// <summary>用户确认：后台逐个优雅关闭，不阻塞 UI。</summary>
    public void ConfirmCommitRelief()
    {
        var candidates = _l3Candidates;
        CommitReliefPending = false;
        if (candidates == null || candidates.Count == 0) return;
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
                SetActionStatus(UiStrings.F("Relief_ClosedSummary", ok, candidates.Count));
                await RefreshAsync();
            });
        });
    }

    /// <summary>用户放弃：记决策日志，不动任何进程。</summary>
    public void DismissCommitRelief()
    {
        CommitReliefPending = false;
        SetActionStatus(UiStrings.Get("Relief_GaveUp"));
        Decisions.Log(new DecisionEntry
        {
            Kind = "CommitRelief",
            Target = "L3Request",
            Decision = "放弃",
            Reason = "用户在确认卡上选择了放弃"
        });
    }


    private bool _longOp;
    private string _longOpText = "";
    /// <summary>长操作进行中（腾内存执行/清扫/批量处置）。区别于每 10 秒一次的刷新 busy——
    /// 进度条只在这种时刻亮，否则状态栏又变成新的闪烁噪音。</summary>
    public bool LongOp
    {
        get => _longOp;
        set => SetUi(() => { _longOp = value; OnPropertyChanged(nameof(LongOpText)); });
    }
    /// <summary>长操作的进度文案（"3/12 — python"）。空串时状态栏显示占位。</summary>
    public string LongOpText
    {
        get => _longOpText;
        set => SetUi(() => _longOpText = value);
    }

    // ---- 低可用兜底：温和档 + 继续确认（v0.24.0）----
    //
    // 真机 18:17 的教训：一次压 152 个安全子集、回收 4 GB，用户体感却是"清理后变卡"——
    // 压缩把 152 个进程的页面换出，下次访问要读回。压缩没有撤销 API，
    // 所以只能"少压 + 说清楚"：先进温和档（20 个），再问要不要继续。

    private System.Windows.Threading.DispatcherTimer? _floorTrimTimer;

    private bool _floorTrimPending;
    /// <summary>是否显示"继续压缩剩余候选"的确认卡。</summary>
    public bool FloorTrimPending
    {
        get => _floorTrimPending;
        private set => SetUi(() => _floorTrimPending = value);
    }

    private int _floorTrimRemaining;
    /// <summary>温和档之后还剩多少个候选没压。</summary>
    public int FloorTrimRemaining
    {
        get => _floorTrimRemaining;
        private set => SetUi(() => _floorTrimRemaining = value);
    }

    private long _floorTrimReclaimMb;
    /// <summary>继续压缩的预计释放（按剩余候选的私有工作集合计，是上界）。</summary>
    public long FloorTrimReclaimMb
    {
        get => _floorTrimReclaimMb;
        private set => SetUi(() => _floorTrimReclaimMb = value);
    }

    private double _floorTrimAvailGb;
    /// <summary>触发时的可用内存，给用户一个"为什么动手"的坐标。</summary>
    public double FloorTrimAvailGb
    {
        get => _floorTrimAvailGb;
        private set => SetUi(() => _floorTrimAvailGb = value);
    }

    /// <summary>引擎低可用兜底的四个阶段。文案在 App 层，引擎只传数值（i18n 纪律）。</summary>
    private void HandleFloorTrim(ResidentEngine.FloorTrimNotice n)
    {
        switch (n.Stage)
        {
            case ResidentEngine.FloorTrimStage.MildDone:
                RaiseNotice(UiStrings.Get("Floor_MildTitle"),
                    UiStrings.F("Floor_MildBody", n.Count, n.FreedMb, n.AvailGb));
                break;

            case ResidentEngine.FloorTrimStage.FullDone:
                RaiseNotice(UiStrings.Get("Floor_FullTitle"),
                    UiStrings.F("Floor_FullBody", n.Count, n.FreedMb, n.AvailGb));
                break;

            case ResidentEngine.FloorTrimStage.SkippedByUser:
                RaiseNotice(UiStrings.Get("Floor_SkippedTitle"),
                    UiStrings.F("Floor_SkippedBody", n.Count, n.AvailGb));
                break;

            case ResidentEngine.FloorTrimStage.SuggestContinue:
                // 托盘模式不弹卡：温和压缩已经做过了，无人可问时不该挡住任何东西。
                // 与 L3 卡的"用户不在场 → 放弃"语义不同：L3 放弃 = 什么都不做，
                // 这里放弃 = 维持已经生效的温和档。
                if (!UiActive)
                {
                    Decisions.Log(new DecisionEntry
                    {
                        Kind = "CommitRelief", Target = "FloorTrim", Decision = "跳过",
                        Code = "floor_trim.deferred",
                        Args = new[] { n.Count.ToString() },
                        Reason = $"用户不在场（托盘模式），保持温和档；剩余候选 {n.Count} 个未处理"
                    });
                    return;
                }
                if (FloorTrimPending) return;           // 已有卡在等，不叠加
                FloorTrimRemaining = n.Count;
                FloorTrimReclaimMb = n.FreedMb;
                FloorTrimAvailGb = n.AvailGb;
                FloorTrimPending = true;
                StartFloorTrimCountdown();
                break;
        }
    }

    /// <summary>20 秒无响应 = 不继续。已经温和压过一轮，超时是安全默认。</summary>
    private void StartFloorTrimCountdown()
    {
        _floorTrimTimer?.Stop();
        _floorTrimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _floorTrimTimer.Tick += (_, _) =>
        {
            _floorTrimTimer?.Stop();
            if (!FloorTrimPending) return;
            FloorTrimPending = false;
            Decisions.Log(new DecisionEntry
            {
                Kind = "CommitRelief", Target = "FloorTrim", Decision = "超时",
                Code = "floor_trim.deferred",
                Args = new[] { FloorTrimRemaining.ToString() },
                Reason = $"继续压缩确认卡 {FloorTrimRemaining} 个候选、20 秒无响应，保持温和档"
            });
        };
        _floorTrimTimer.Start();
    }

    /// <summary>用户确认继续压缩剩余候选。用最新快照重算，不信任 20 秒前的数据（PID 可能已复用）。</summary>
    public void ConfirmFloorTrimContinue()
    {
        FloorTrimPending = false;
        _floorTrimTimer?.Stop();

        var audio = new HashSet<int>(_lastProcs.Where(p => p.AudioActive).Select(p => p.Pid));
        var safe = TrimSelection.BuildSafeTrimSet(_lastProcs, Engine.LastForegroundPid, audio,
            releaseRatio: Cleanup.TrimFeedback.RatioOf);
        var r = Cleanup.TrimWorkingSets(safe);
        Decisions.Log(new DecisionEntry
        {
            Kind = "CommitRelief", Target = "FloorTrim", Decision = "继续压缩",
            Code = "floor_trim.continue",
            Args = new[] { r.Trimmed.ToString(), (r.FreedBytes / 1048576).ToString() },
            Reason = $"用户确认继续：重算候选后压缩 {r.Trimmed} 个（含已温和压缩过的进程；" +
                     "EmptyWorkingSet 对已空的进程近似零成本）"
        });
        SetActionStatus(UiStrings.F("Floor_ContinueDone", r.Trimmed, r.FreedBytes / 1048576.0));
    }

    /// <summary>用户拒绝继续压缩。</summary>
    public void DismissFloorTrimContinue()
    {
        FloorTrimPending = false;
        _floorTrimTimer?.Stop();
        Decisions.Log(new DecisionEntry
        {
            Kind = "CommitRelief", Target = "FloorTrim", Decision = "跳过",
            Code = "floor_trim.declined",
            Reason = "用户选择不继续压缩（已温和压缩的进程不回滚；页面会在下次访问时自然读回）"
        });
    }

    /// <summary>
    /// 解释型通知的统一入口（v0.24.0）。走托盘 + 状态栏，**不占主页横幅**：
    /// 横幅只在主页可见、且 `RaiseAlert` 每次直接覆盖（无队列），
    /// 用它解释"刚才为什么卡"反而会把"内存仍然告急"盖掉；而"卡"的体感跨页面，托盘才看得见。
    ///
    /// 只给新路径用。老的 `RaiseAlert`/`AlertRaised` 分裂问题（垃圾建议发不出托盘）
    /// 单独处理，不在功能提交里混重构。
    /// </summary>
    private void RaiseNotice(string title, string text)
    {
        if (!ConfigStore.Config.ShowAlerts)
        {
            Decisions.Log(new DecisionEntry { Kind = "Diag", Target = title, Reason = text });
            return;
        }
        AlertRaised?.Invoke(title, text, false);   // 托盘气球（MainWindow 按标题前缀做 30 分钟冷却）
        SetActionStatus(text);
    }
}
