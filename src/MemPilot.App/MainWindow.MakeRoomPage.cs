namespace MemPilot.App;

public partial class MainWindow : Window
{
    private async void HotkeyActivateMakeRoom()
    {
        ShowWindow();
        SwitchToTab("🚀");
        await BuildMakeRoomFromHomeAsync();
    }

    private async Task BuildMakeRoomFromHomeAsync()
    {
        double gb = 8;
        if (HomeTargetGB != null) double.TryParse(HomeTargetGB.Text, out gb);
        if (gb <= 0) gb = 8;
        if (TargetAvailGB != null) TargetAvailGB.Text = gb.ToString("0.#");
        await BuildAndShowPlanAsync(gb);
    }

    private void OnCompactMakeRoom()
    {
        ShowWindow();
        SwitchToTab("🚀");
        _ = BuildMakeRoomFromHomeAsync();
    }

    // ---- make-room workflow ----

    private async void PlanMakeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (TargetAvailGB == null) return;
        if (!double.TryParse(TargetAvailGB.Text, out var targetGB) || targetGB <= 0) { _vm.Status = "目标可用内存请填正数（GB）。"; return; }
        await BuildAndShowPlanAsync(targetGB);
    }

    private async void HomePlan_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(HomeTargetGB.Text, out var targetGB) || targetGB <= 0) { _vm.Status = "请先在主页填写需要的内存（GB）。"; return; }
        // home CTA keeps the same safety: build the plan, review it on the 腾内存 page before executing
        SwitchToTab("🚀"); // realize the tab's controls first
        if (TargetAvailGB != null) TargetAvailGB.Text = targetGB.ToString("0.#");
        await BuildAndShowPlanAsync(targetGB);
    }

    private async Task BuildAndShowPlanAsync(double targetGB)
    {
        double.TryParse(TargetCommitGB?.Text, out var commitGB);
        var req = new MakeRoomRequest
        {
            TargetAvailableGB = targetGB,
            EnsureCommitToo = EnsureCommit?.IsChecked != false,
            TargetCommitFreeGB = commitGB <= 0 ? targetGB : commitGB,
            AllowKill = AllowKill?.IsChecked == true,
            IncludeTrim = IncludeTrim?.IsChecked != false
        };
        _vm.Status = "正在生成腾挪计划...";
        var plan = await _vm.BuildMakeRoomPlanAsync(req);
        _currentPlan = plan;
        PlanGrid.ItemsSource = plan.Items;
        MakeRoomSummary.Text = plan.SummaryText + (plan.CoversPhysicalGap || plan.PhysicalGapBytes == 0 ? "" : "  ⚠ 计划不足以覆盖目标，请勾选允许 L4 或降低目标。");
        ExecutePlanBtn.IsEnabled = plan.Items.Count > 0;
        _vm.Status = plan.Items.Count > 0
            ? "腾挪计划已生成，请核对下面的名单后点「执行计划」。"
            : "内存已经够用，不需要腾挪。";
    }

    private async void ExecuteMakeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.Items.Count == 0) return;
        var lines = string.Join("\n", _currentPlan.Items.Select(i =>
            $"{i.ActionText}  {i.Process.Name} (PID {i.Process.Pid})  预计物理 {i.EstimatedPhysicalReclaimBytes / 1048576.0:F0} MB"
            + (i.ChildCount > 0 ? $"（含 {i.ChildCount} 子进程）" : "")));
        var res = MessageBox.Show($"将执行 {_currentPlan.Items.Count} 项操作：\n\n{lines}\n\n" +
            (_currentPlan.Items.Any(i => i.KillIfCloseFails) ? "部分 L3 项在关闭超时后会升级为强制结束。\n" : "") +
            "继续？", "确认执行腾挪计划", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        ExecutePlanBtn.IsEnabled = false;
        CancelPlanBtn.IsEnabled = true;
        _vm.Status = "正在执行腾挪计划...";
        _planCts = new System.Threading.CancellationTokenSource();
        try
        {
            var result = await _vm.ExecuteMakeRoomAsync(_currentPlan, _planCts.Token);
            MakeRoomResultText.Text = result.SummaryText;
            _vm.Status = "腾挪计划执行完毕。" + result.SummaryText;
            _tray?.ShowBalloon("腾挪完成", result.SummaryText);
        }
        catch (OperationCanceledException)
        {
            _vm.Status = "腾挪计划已取消（已执行的项目不可回退，剩余项目未执行）。";
            MakeRoomResultText.Text = "已取消。";
        }
        finally
        {
            _planCts.Dispose();
            _planCts = null;
            CancelPlanBtn.IsEnabled = false;
            _currentPlan = null;
            PlanGrid.ItemsSource = null;
            await _vm.RefreshAsync();
        }
    }

    private void CancelMakeRoom_Click(object sender, RoutedEventArgs e) => _planCts?.Cancel();

    private void ClearMakeRoom_Click(object sender, RoutedEventArgs e)
    {
        _currentPlan = null;
        PlanGrid.ItemsSource = null;
        MakeRoomSummary.Text = "";
        MakeRoomResultText.Text = "";
        ExecutePlanBtn.IsEnabled = false;
    }
}
