using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

/// <summary>
/// WSL 常驻提示卡（v0.22.0）：vmmemWSL 占用够大且今天零活动 → 建议 `wsl --shutdown`。
/// 只提示 + 复制命令：不挂起、不结束、不自动执行。
/// </summary>
public partial class MainViewModel
{
    private WslHint? _wslHint;

    private bool _wslHintPending;
    public bool WslHintPending
    {
        get => _wslHintPending;
        private set => SetUi(() => _wslHintPending = value);
    }

    /// <summary>卡片正文（含 GB 与已运行小时）。</summary>
    public string WslHintText => _wslHint == null
        ? ""
        : UiStrings.F("Wsl_Body", _wslHint.TotalGb, _wslHint.RunningHours);

    private void AttachWslHint()
    {
        Engine.WslHintAdvised += hint => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!UiActive) return;   // 引擎已按 UiActive 门控，这里是竞态兜底
            _wslHint = hint;
            OnPropertyChanged(nameof(WslHintText));
            WslHintPending = true;
            Decisions.Log(new DecisionEntry
            {
                Kind = "WslHint",
                Target = $"{hint.Process.Name} (PID {hint.Process.Pid})",
                Decision = DecisionLogFilter.DecisionAdvice,
                Reason = UiStrings.F("Wsl_DecisionReason", hint.TotalGb, hint.RunningHours)
            });
        }));
    }

    /// <summary>「知道了」：写学习账本（忽略 7→30→90 天），不动 WSL。</summary>
    public void DismissWslHint()
    {
        var hint = _wslHint;
        WslHintPending = false;
        _wslHint = null;
        OnPropertyChanged(nameof(WslHintText));
        if (hint != null) Engine.JunkAdvice?.RecordDismissed(hint.Process.Name, DateTime.UtcNow);
    }

    /// <summary>复制 `wsl --shutdown`（只写剪贴板，绝不执行）。</summary>
    public void CopyWslShutdownCommand()
    {
        try
        {
            System.Windows.Clipboard.SetText("wsl --shutdown");
            SetActionStatus(UiStrings.Get("Wsl_Copied"));
        }
        catch (Exception ex) { Diag.Record("wsl-hint:clipboard", ex); }
    }
}
