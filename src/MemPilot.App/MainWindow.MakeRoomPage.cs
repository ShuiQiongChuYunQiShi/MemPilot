namespace MemPilot.App;

public partial class MainWindow : Window
{
    private async void HotkeyActivateMakeRoom()
    {
        // v0.11.0 P1-2：热键只唤起+跳页+聚焦，**不再自动生成 8GB 计划**——
        // 原来是"先斩后奏"：用户按热键只是想看看，计划已经生成好了（还用硬编码的 8GB）。
        ShowWindow();
        SwitchToTab("makeroom");
        await Task.Delay(50); // 等页签实体化
        TargetAvailGB?.Focus();
        TargetAvailGB?.SelectAll();
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
        SwitchToTab("makeroom");
        _ = BuildMakeRoomFromHomeAsync();
    }

    // ---- make-room workflow ----

    private async void PlanMakeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (TargetAvailGB == null) return;
        if (!double.TryParse(TargetAvailGB.Text, out var targetGB) || targetGB <= 0) { _vm.Status = UiStrings.Get("MakeRoom_TargetPositive"); return; }
        await BuildAndShowPlanAsync(targetGB);
    }

    private async void HomePlan_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(HomeTargetGB.Text, out var targetGB) || targetGB <= 0) { _vm.Status = UiStrings.Get("MakeRoom_FillTarget"); return; }
        // home CTA keeps the same safety: build the plan, review it on the 腾内存 page before executing
        SwitchToTab("makeroom"); // realize the tab's controls first
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
        _vm.Status = UiStrings.Get("MakeRoom_Generating");
        var plan = await _vm.BuildMakeRoomPlanAsync(req);
        _currentPlan = plan;
        // 页签懒加载：控件未实体化时不要 NRE（v0.9.0 修：路由键错曾导致这里空引用崩溃）
        if (PlanGrid == null || MakeRoomSummary == null || ExecutePlanBtn == null)
        {
            _vm.Status = UiStrings.F("MakeRoom_PlanReadyOtherPage", plan.Items.Count);
            return;
        }
        PlanGrid.ItemsSource = plan.Items;
        MakeRoomSummary.Text = plan.SummaryText + (plan.CoversPhysicalGap || plan.PhysicalGapBytes == 0 ? "" : UiStrings.Get("MakeRoom_Insufficient"));
        ExecutePlanBtn.IsEnabled = plan.Items.Count > 0;
        _vm.Status = plan.Items.Count > 0
            ? UiStrings.Get("MakeRoom_PlanReady")
            : UiStrings.Get("MakeRoom_AlreadyEnough");
    }

    private async void ExecuteMakeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.Items.Count == 0) return;
        var lines = string.Join("\n", _currentPlan.Items.Select(i =>
            UiStrings.F("MakeRoom_PlanLine", i.ActionText, i.Process.Name, i.Process.Pid,
                i.EstimatedPhysicalReclaimBytes / 1048576.0)
            + (i.ChildCount > 0 ? UiStrings.F("MakeRoom_PlanLineChildren", i.ChildCount) : "")));
        var escalateNote = _currentPlan.Items.Any(i => i.KillIfCloseFails)
            ? UiStrings.Get("MakeRoom_EscalateNote") : "";
        var res = MessageBox.Show(
            UiStrings.F("MakeRoom_ExecuteConfirm", _currentPlan.Items.Count, lines,
                escalateNote + UiStrings.Get("Common_Continue")),
            UiStrings.Get("Btn_ConfirmExecutePlan"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        ExecutePlanBtn.IsEnabled = false;
        CancelPlanBtn.IsEnabled = true;
        _vm.Status = UiStrings.Get("MakeRoom_Executing");
        _planCts = new System.Threading.CancellationTokenSource();
        _vm.LongOp = true;
        int total = _currentPlan.Items.Count;
        try
        {
            // ExecutePlanAsync 逐项执行并回填 ResultText——轮询它就是真实进度
            var execTask = _vm.ExecuteMakeRoomAsync(_currentPlan, _planCts.Token);
            while (!execTask.IsCompleted)
            {
                int done = _currentPlan.Items.Count(i => i.ResultText.Length > 0);
                _vm.LongOpText = UiStrings.F("MakeRoom_Progress", done, total);
                await Task.Delay(300);
            }
            await execTask;
            var result = execTask.Result;
            MakeRoomResultText.Text = result.SummaryText;
            _vm.Status = UiStrings.Get("MakeRoom_Done") + result.SummaryText;
            // 腾内存完成属于通知白名单（用户主动发起的长操作，切走也该知道结果）
            _tray?.ShowBalloon(UiStrings.Get("MakeRoom_DoneTitle"), result.SummaryText);
        }
        catch (OperationCanceledException)
        {
            _vm.Status = UiStrings.Get("MakeRoom_Cancelled");
            MakeRoomResultText.Text = UiStrings.Get("MakeRoom_CancelledShort");
        }
        finally
        {
            _vm.LongOp = false;
            _vm.LongOpText = "";
            _planCts.Dispose();
            _planCts = null;
            CancelPlanBtn.IsEnabled = false;
            // v0.11.0：不再清空计划表——每项的 ResultText 已回填，保留供复查；
            // 只有用户点「清空」才清。执行按钮保持禁用，防止对旧计划重复执行。
            _currentPlan = null;
            ExecutePlanBtn.IsEnabled = false;
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
