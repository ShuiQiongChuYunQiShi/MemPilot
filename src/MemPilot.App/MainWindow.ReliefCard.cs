namespace MemPilot.App;

/// <summary>提交告急 L3 非模态确认卡（v0.11.0 P1-3）的按钮处理。</summary>
public partial class MainWindow
{
    private void ConfirmCommitRelief_Click(object sender, RoutedEventArgs e) => _vm.ConfirmCommitRelief();
    private void DismissCommitRelief_Click(object sender, RoutedEventArgs e) => _vm.DismissCommitRelief();

    // ---- 垃圾进程建议卡（v0.15.0）----

    private void SuspendJunk2h_Click(object sender, RoutedEventArgs e) => _vm.SuspendJunk(TimeSpan.FromHours(2));
    private void SuspendJunk8h_Click(object sender, RoutedEventArgs e) => _vm.SuspendJunk(TimeSpan.FromHours(8));

    /// <summary>结束进程：无窗口候选走既有 L4 双确认（不绕过安全流程），有窗口的走 L3。</summary>
    private async void CleanupJunk_Click(object sender, RoutedEventArgs e)
    {
        var windowless = _vm.PrepareJunkCleanup();
        var candidates = _vm.JunkAdviceCandidatesSnapshot();
        if (candidates.Count == 0) return;

        // 有窗口 → L3 优雅关闭（逐个确认结果）；无窗口 → 统一一次双确认
        var windowed = candidates.Where(p => p.HasVisibleWindow).ToList();
        var done = new List<int>();
        foreach (var p in windowed)
        {
            var r = _vm.Cleanup.Execute(p, CleanupActionLevel.L3GracefulClose,
                _config.Config.GracefulCloseTimeoutSeconds);
            if (r.Success) done.Add(p.Pid);
        }
        if (windowless.Count > 0)
        {
            var names = string.Join("、", windowless.Select(p => $"{p.Name}(PID {p.Pid})"));
            if (System.Windows.MessageBox.Show(
                    $"以下进程没有可见窗口，无法优雅关闭，只能强制结束：\n\n{names}\n\n该操作不可撤销。继续？",
                    "确认强制结束", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                // 第二次确认（沿用 L4 双确认不变式）
                if (System.Windows.MessageBox.Show(
                        $"第二次确认：确定强制结束这 {windowless.Count} 个进程？",
                        "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Stop) == MessageBoxResult.Yes)
                {
                    foreach (var p in windowless)
                    {
                        var r = _vm.Cleanup.Execute(p, CleanupActionLevel.L4ForceKill, 0);
                        if (r.Success) done.Add(p.Pid);
                    }
                }
            }
        }
        await _vm.FinishJunkCleanupAsync(done);
        _vm.SetActionStatus($"已处理 {done.Count}/{candidates.Count} 个垃圾进程。");
    }

    private void DismissJunkAdvice_Click(object sender, RoutedEventArgs e) => _vm.DismissJunkAdvice();
}
