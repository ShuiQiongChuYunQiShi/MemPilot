using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

/// <summary>
/// 垃圾进程建议卡片（v0.15.0）：整天未使用的垃圾进程 → 主页提示
/// 「挂起 2h / 挂起 8h / 结束 / 忽略」。只建议，动作必须用户点。
/// 结构克隆自提交告急卡（MainViewModel.Relief.cs）。
/// </summary>
public partial class MainViewModel
{
    private JunkAdvice? _junkAdvice;

    private bool _junkAdvicePending;
    /// <summary>是否显示垃圾进程建议卡。</summary>
    public bool JunkAdvicePending
    {
        get => _junkAdvicePending;
        private set => SetUi(() => _junkAdvicePending = value);
    }

    /// <summary>候选名单文案（前 5 个 + "等 N 个"）。</summary>
    public string JunkAdviceNames => _junkAdvice?.NamesText ?? "";

    /// <summary>预计释放的私有提交（MB，取整）。</summary>
    public long JunkAdviceTotalMb => (_junkAdvice?.TotalPrivateBytes ?? 0) / 1048576;

    /// <summary>引擎事件 → 卡片（窗口不可见只记诊断、不写决策日志）。</summary>
    private void AttachJunkAdvice()
    {
        Engine.JunkAdvised += advice => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!UiActive)
            {
                // v0.20.0（P0）：引擎已在源头按 UiActive 挡住评估，这里是竞态兜底（窗口刚隐藏）。
                // 只设 15 分钟冷却 + 写诊断（决定日志不再记录这种"用户没看到"的事件，占 87.7% 噪声）。
                Engine.DeferJunkAdvice(ResidentEngine.JunkAdviceRetryCooldown);
                Diag.Record("junk-advice:deferred", $"window not visible; retry after {ResidentEngine.JunkAdviceRetryCooldown.TotalMinutes:F0}min");
                return;
            }
            // 真正展示前才写"已建议"冷却（引擎不再提前写）
            foreach (var p in advice.Candidates)
                Engine.JunkAdvice?.RecordSuggested(p.Name, DateTime.UtcNow);
            _junkAdvice = advice;
            OnPropertyChanged(nameof(JunkAdviceNames));
            OnPropertyChanged(nameof(JunkAdviceTotalMb));
            JunkAdvicePending = true;
            // v0.20.0：日志改在这里写——必然真交付（旧实现引擎写、VM 回滚，日志与事实不符）
            Decisions.Log(new DecisionEntry
            {
                Kind = "JunkAdvice",
                Target = advice.NamesText,
                Decision = "建议",
                Reason = advice.Reason + $"；预计释放 {advice.TotalPrivateBytes / 1048576.0:F0} MB"
            });
            // 每日一次托盘气球（通知策略第 5 类白名单）
            RaiseAlert(UiStrings.Get("Junk_BalloonTitle"), UiStrings.F("Junk_BalloonBody", advice.NamesText, JunkAdviceTotalMb));
        }));
    }

    /// <summary>最近一次刷新得到的进程快照（设置页「立即检查」用）。</summary>
    public List<ProcessInfo> LastProcessSnapshot() => _lastProcs.ToList();

    /// <summary>当前候选快照（供 UI 的结束流程读取）。</summary>
    public List<ProcessInfo> JunkAdviceCandidatesSnapshot()
        => _junkAdvice?.Candidates.ToList() ?? new List<ProcessInfo>();

    /// <summary>挂起 N 小时（2h / 8h 两档），到期自动恢复。</summary>
    public void SuspendJunk(TimeSpan hold)
    {
        var advice = _junkAdvice;
        JunkAdvicePending = false;
        _junkAdvice = null;                      // v0.17.0：清空，避免快照读到已处理候选
        OnPropertyChanged(nameof(JunkAdviceNames));
        OnPropertyChanged(nameof(JunkAdviceTotalMb));
        if (advice == null) return;
        _ = Task.Run(async () =>
        {
            int ok = 0;
            foreach (var p in advice.Candidates)
            {
                var r = Cleanup.SuspendTimed(p, UiStrings.F("Junk_SuspendReason", hold.TotalHours), hold);
                if (r.Success) ok++;
                if (r.Success) Engine.JunkAdvice?.RecordAccepted(p.Name, DateTime.UtcNow);
                Decisions.Log(new DecisionEntry
                {
                    Kind = "JunkAdvice",
                    Target = $"{p.Name} (PID {p.Pid})",
                    Decision = r.Success ? "挂起" : "挂起失败",
                    Reason = $"{r.Message}（{hold.TotalHours:F0} 小时后自动恢复）"
                });
            }
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                SetActionStatus(UiStrings.F("Junk_SuspendedSummary", ok, advice.Candidates.Count, hold.TotalHours));
                await RefreshAsync();
            });
        });
    }

    /// <summary>
    /// 立即结束。有窗口 → L3 优雅关闭；无窗口 → 复用既有 L4 双确认（不绕过安全流程）。
    /// 返回需要交给 UI 执行双确认的候选（无窗口的那些）。
    /// </summary>
    public List<ProcessInfo> PrepareJunkCleanup()
    {
        var advice = _junkAdvice;
        JunkAdvicePending = false;
        if (advice == null) return new List<ProcessInfo>();
        return advice.Candidates.Where(p => !p.HasVisibleWindow).ToList();
    }

    /// <summary>
    /// 结束流程的公共收尾：记录接受 + 刷新。
    /// v0.17.0：接收**捕获的候选快照**——L4 双确认是模态框（泵消息），期间可能到达新的
    /// JunkAdvised 事件替换 _junkAdvice，用实时字段会把 pid 归到错误的名字上。
    /// </summary>
    public async Task FinishJunkCleanupAsync(List<int> cleanedPids, List<ProcessInfo> captured)
    {
        foreach (var pid in cleanedPids)
        {
            var name = captured.FirstOrDefault(p => p.Pid == pid)?.Name;
            if (!string.IsNullOrEmpty(name)) Engine.JunkAdvice?.RecordAccepted(name, DateTime.UtcNow);
        }
        _junkAdvice = null;
        OnPropertyChanged(nameof(JunkAdviceNames));
        OnPropertyChanged(nameof(JunkAdviceTotalMb));
        await RefreshAsync();
    }

    /// <summary>忽略：写学习账本（冷却升级 7→30→90 天），不动任何进程。</summary>
    public void DismissJunkAdvice()
    {
        var advice = _junkAdvice;
        JunkAdvicePending = false;
        _junkAdvice = null;
        OnPropertyChanged(nameof(JunkAdviceNames));
        OnPropertyChanged(nameof(JunkAdviceTotalMb));
        if (advice == null) return;
        foreach (var p in advice.Candidates)
        {
            Engine.JunkAdvice?.RecordDismissed(p.Name, DateTime.UtcNow);
            int n = Engine.JunkAdvice?.DismissCountOf(p.Name) ?? 1;
            var cool = JunkAdviceStore.CooldownFor(n);
            Decisions.Log(new DecisionEntry
            {
                Kind = "JunkAdvice",
                Target = p.Name,
                Decision = "忽略",
                Reason = $"已学会：{p.Name} 在 {cool.TotalDays:F0} 天内不再提示（累计忽略 {n} 次）"
            });
        }
        SetActionStatus(UiStrings.F("Junk_Ignored", advice.Candidates.Count));
    }
}
