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

    /// <summary>引擎事件 → 卡片（托盘模式只记日志不打扰）。</summary>
    private void AttachJunkAdvice()
    {
        Engine.JunkAdvised += advice => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!UiActive)
            {
                Decisions.Log(new DecisionEntry
                {
                    Kind = "JunkAdvice",
                    Target = advice.NamesText,
                    Decision = "跳过",
                    Reason = "用户不在场（托盘模式），未弹卡打扰——次日仍会重新评估"
                });
                return;
            }
            _junkAdvice = advice;
            OnPropertyChanged(nameof(JunkAdviceNames));
            OnPropertyChanged(nameof(JunkAdviceTotalMb));
            JunkAdvicePending = true;
            // 每日一次托盘气球（通知策略第 5 类白名单）
            RaiseAlert("垃圾进程建议", $"整天未使用：{advice.NamesText}（约 {JunkAdviceTotalMb} MB）。可在主页选择挂起或结束。");
        }));
    }

    /// <summary>当前候选快照（供 UI 的结束流程读取）。</summary>
    public List<ProcessInfo> JunkAdviceCandidatesSnapshot()
        => _junkAdvice?.Candidates.ToList() ?? new List<ProcessInfo>();

    /// <summary>挂起 N 小时（2h / 8h 两档），到期自动恢复。</summary>
    public void SuspendJunk(TimeSpan hold)
    {
        var advice = _junkAdvice;
        JunkAdvicePending = false;
        OnPropertyChanged(nameof(JunkAdviceNames));
        OnPropertyChanged(nameof(JunkAdviceTotalMb));
        if (advice == null) return;
        _ = Task.Run(async () =>
        {
            int ok = 0;
            foreach (var p in advice.Candidates)
            {
                var r = Cleanup.SuspendTimed(p, $"垃圾进程建议：整天未使用，挂起 {hold.TotalHours:F0} 小时", hold);
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
                SetActionStatus($"已定时挂起 {ok}/{advice.Candidates.Count} 个垃圾进程（{hold.TotalHours:F0} 小时后自动恢复）。");
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

    /// <summary>结束流程的公共收尾：记录接受 + 刷新。</summary>
    public async Task FinishJunkCleanupAsync(List<int> cleanedPids)
    {
        foreach (var pid in cleanedPids)
        {
            var name = _junkAdvice?.Candidates.FirstOrDefault(p => p.Pid == pid)?.Name;
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
        SetActionStatus($"已忽略本次垃圾进程建议（已记住：{advice.Candidates.Count} 个进程暂停提示）。");
    }
}
