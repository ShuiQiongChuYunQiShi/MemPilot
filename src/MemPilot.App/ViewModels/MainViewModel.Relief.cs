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
            var names = string.Join("、", c.Take(6).Select(p => $"{p.Name}(PID {p.Pid})"));
            return names + (c.Count > 6 ? $" 等 {c.Count} 个" : "");
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
                SetActionStatus($"提交兜底：已优雅关闭 {ok}/{candidates.Count} 个闲置后台。");
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
}
