using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using MemoryTool.Models;
using MemoryTool.Services;
using MemoryTool.ViewModels;
using Application = System.Windows.Application;
using TabControl = System.Windows.Controls.TabControl;
using RadioButton = System.Windows.Controls.RadioButton;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace MemoryTool.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ConfigStore _config;
    private readonly NativeMemoryCleanerService _memCleaner = new();
    private NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconObj;
    private IntPtr _trayHicon = IntPtr.Zero;
    private bool _allowClose;
    private bool _isDarkTheme;
    private System.Windows.Threading.DispatcherTimer? _trayUpdateTimer;
    private MakeRoomPlan? _currentPlan;
    private System.Threading.CancellationTokenSource? _planCts;

    // selection preserved across refreshes (collections are rebuilt each cycle)
    private readonly HashSet<int> _selectedProcessPids = new();
    private readonly HashSet<int> _selectedUnusedPids = new();

    public MainWindow()
    {
        InitializeComponent();
        var dataDir = App.ResolveDataDir();
        _config = new ConfigStore(dataDir);
        var audit = new AuditLogger(dataDir);
        var feedback = new FeedbackStore(dataDir);
        _vm = new MainViewModel(_config, audit, feedback);
        DataContext = _vm;

        SyncModeRadios(_vm.CurrentMode);

        // per-tab controls are initialized lazily when their tab is first shown
        // (TabControl content is instantiated on selection)

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MemoryTool",
            Visible = true
        };
        _trayIcon.DoubleClick += (s, e) => ShowWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示", null, (s, e) => ShowWindow());
        menu.Items.Add("刷新", null, async (s, e) => await _vm.RefreshAsync());
        menu.Items.Add("退出", null, (s, e) => { _allowClose = true; Application.Current.Shutdown(); });
        _trayIcon.ContextMenuStrip = menu;

        // auto mode switch happens in the engine — reflect it back into the combo box
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentMode)) SyncModeRadios(_vm.CurrentMode);
        };

        if (_config.Config.StartMinimizedToTray)
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Loaded += (s2, e2) => { Hide(); ShowInTaskbar = true; };
        }

        // announce automatic mode switches in the tray (background thread -> marshal)
        _vm.Engine.ModeChanged += (s, e) =>
            Dispatcher.InvokeAsync(() => _trayIcon?.ShowBalloonTip(3000, "模式已切换",
                $"已自动切换到 {e.Mode}（{e.Reason}），详见决策日志。", ToolTipIcon.Info));

        Closing += MainWindow_Closing;
        _vm.CollectionsRebuilt += (s, e) => RestoreSelection();
        Loaded += async (s, e) =>
        {
            SyncModeRadios(_vm.CurrentMode);
            if (AutoSwitchCheck != null) AutoSwitchCheck.IsChecked = _config.Config.AutoModeSwitch;
            await _vm.RefreshAsync();
            _vm.StartEngine();          // resident engine: periodic refresh + policies
            StartTrayUpdateTimer();
        };
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    // ---- selection preservation ----

    private void RestoreSelection()
    {
        if (ProcessGrid == null || UnusedGrid == null) return;
        try
        {
            foreach (var p in _vm.FilteredProcesses.Where(p => _selectedProcessPids.Contains(p.Pid)).ToList())
                ProcessGrid.SelectedItems.Add(p);
            foreach (var c in _vm.UnusedCandidates.Where(c => _selectedUnusedPids.Contains(c.Process.Pid)).ToList())
                UnusedGrid.SelectedItems.Add(c);
        }
        catch { }
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var p in e.RemovedItems.OfType<ProcessInfo>()) _selectedProcessPids.Remove(p.Pid);
        foreach (var p in e.AddedItems.OfType<ProcessInfo>()) _selectedProcessPids.Add(p.Pid);
    }

    private void UnusedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var c in e.RemovedItems.OfType<UnusedCandidate>()) _selectedUnusedPids.Remove(c.Process.Pid);
        foreach (var c in e.AddedItems.OfType<UnusedCandidate>()) _selectedUnusedPids.Add(c.Process.Pid);
    }

    // ---- top bar ----

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        var targets = _vm.UnusedCandidates.ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("当前没有可清理候选（观察期可能未满）。", "提示");
            return;
        }
        foreach (var c in targets)
            await _vm.PerformCleanupAsync(c, CleanupActionLevel.L1Trim);
        await _vm.RefreshAsync();
    }

    private void ToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private bool _syncingModeRadios;

    private void SyncModeRadios(AppMode mode)
    {
        if (ModeRbResident == null || ModeRbDevelopment == null || ModeRbGame == null ||
            ModeRbAiGeneration == null || ModeRbBalanced == null) return; // home tab not realized yet
        _syncingModeRadios = true;
        try
        {
            var map = new Dictionary<AppMode, RadioButton>
            {
                [AppMode.Resident] = ModeRbResident,
                [AppMode.Development] = ModeRbDevelopment,
                [AppMode.Game] = ModeRbGame,
                [AppMode.AiGeneration] = ModeRbAiGeneration,
                [AppMode.Balanced] = ModeRbBalanced
            };
            foreach (var kv in map) kv.Value.IsChecked = kv.Key == mode;
        }
        finally { _syncingModeRadios = false; }
    }

    private void HomeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingModeRadios) return;
        if (sender is RadioButton { Tag: string s } && Enum.TryParse<AppMode>(s, out var m))
            _vm.CurrentMode = m;
    }

    private void AutoSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingModeRadios || AutoSwitchCheck == null) return;
        _config.Config.AutoModeSwitch = AutoSwitchCheck.IsChecked == true;
        _config.Save();
        _vm.Status = _config.Config.AutoModeSwitch ? "已开启模式自动切换。" : "已关闭模式自动切换。";
    }

    // ---- home navigation ----

    private void SwitchToTab(string headerPrefix)
    {
        foreach (var item in RootTabs.Items.OfType<TabItem>())
        {
            var header = item.Header?.ToString();
            if (header == null) continue;
            if (header.Replace(" ", "").StartsWith(headerPrefix, StringComparison.Ordinal))
            {
                RootTabs.SelectedItem = item;
                return;
            }
        }
    }

    private void GoClean_Click(object sender, RoutedEventArgs e) => SwitchToTab("🧹");
    private void GoDecisions_Click(object sender, RoutedEventArgs e) => SwitchToTab("🧠");

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _vm.SearchText = SearchBox.Text;

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return;
        var header = e.AddedItems.OfType<TabItem>().FirstOrDefault()?.Header?.ToString()?.Replace(" ", "") ?? "";
        if (header.StartsWith("🧵")) _vm.RebuildThreads();
        else if (header.StartsWith("🎚")) InitModeTab();
        else if (header.StartsWith("💬")) InitFeedbackTab();
        else if (header.StartsWith("⚙")) InitSettingsTab();
    }

    private bool _modeTabInit;
    private void InitModeTab()
    {
        if (_modeTabInit || ModeList == null) return;
        _modeTabInit = true;
        foreach (var kv in _config.Config.ModeProfiles)
            ModeList.Items.Add(new ListBoxItem { Content = $"{kv.Value.Name} ({kv.Key})", Tag = kv.Key });
    }

    private void InitFeedbackTab()
    {
        if (FbCategory == null || FbCategory.Items.Count > 0) return;
        foreach (ProcessCategory c in Enum.GetValues(typeof(ProcessCategory)))
            FbCategory.Items.Add(c);
        FbCategory.SelectedItem = ProcessCategory.ThirdPartyApp;
    }

    private bool _settingsTabInit;
    private void InitSettingsTab()
    {
        if (_settingsTabInit || SetCpu == null) return;
        _settingsTabInit = true;
        SetCpu.Text = _config.Config.UnusedCpuThresholdPercent.ToString();
        SetIdle.Text = _config.Config.UnusedIdleMinutes.ToString();
        SetRefresh.Text = _config.Config.RefreshIntervalSeconds.ToString();
        SetMinAge.Text = _config.Config.MinProcessAgeSeconds.ToString();
        SetObservations.Text = _config.Config.RequiredIdleObservations.ToString();
        SetGraceful.Text = _config.Config.GracefulCloseTimeoutSeconds.ToString();
        SetEndpoint.Text = _config.Config.OnlineLookupEndpoint;
        SetApiKey.Text = _config.Config.OnlineLookupApiKey;
        SetOnlineEnabled.IsChecked = _config.Config.OnlineLookupEnabled;
        SetAutoCleanup.IsChecked = _config.Config.AutoCleanupEnabled;
        SetAutoClose.IsChecked = _config.Config.AutoCloseCleanMatches;
        SetAutoOptimize.IsChecked = _config.Config.AutoOptimizeEnabled;
        SetAutoOptimizeInterval.IsChecked = _config.Config.AutoOptimizeIntervalEnabled;
        SetSkipFullscreen.IsChecked = _config.Config.SkipOptimizeInFullscreen;
        SetAutoStart.IsChecked = StartupService.IsAutoStart();
        SetStartMinimized.IsChecked = _config.Config.StartMinimizedToTray;
        ProtectedListEditor.Text = string.Join("\n", _config.Config.ProtectedNames);
        WhitelistEditor.Text = string.Join("\n", _config.Config.Whitelist);
        BlacklistEditor.Text = string.Join("\n", _config.Config.Blacklist);
    }

    // ---- cleanup actions ----

    private UnusedCandidate? GetSelectedUnused()
    {
        if (UnusedGrid != null && UnusedGrid.SelectedItem is UnusedCandidate uc) return uc;
        // context menu may target the process grid
        if (ProcessGrid != null && ProcessGrid.SelectedItem is ProcessInfo pi)
            return new UnusedCandidate
            {
                Process = pi,
                Reasons = new List<string> { "手动选择" },
                EstimatedReclaimableBytes = pi.Memory.PrivateWorkingSetBytes,
                EstimatedCommitReclaimableBytes = pi.Memory.PrivateBytes,
                Risk = ProcessRiskLevel.Medium,
                SuggestedLevel = CleanupActionLevel.L1Trim
            };
        return null;
    }

    private async Task RunCleanup(UnusedCandidate c, CleanupActionLevel level)
    {
        if (level == CleanupActionLevel.L4ForceKill)
        {
            var msg = $"强制结束 {c.Process.Name} (PID {c.Process.Pid})？\n预计释放: {c.EstimatedReclaimableBytes / 1048576.0:F0} MB\n该操作不可撤销。";
            if (MessageBox.Show(msg, "确认 L4", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (MessageBox.Show("第二次确认：确定强制结束？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        else if (level == CleanupActionLevel.L2Suspend)
        {
            if (MessageBox.Show($"挂起 {c.Process.Name}？挂起只冻结 CPU，不释放内存，可在“已挂起”页恢复。",
                "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }
        else if (level == CleanupActionLevel.L3GracefulClose)
        {
            if (MessageBox.Show($"向 {c.Process.Name} (PID {c.Process.Pid}) 的窗口发送关闭消息？程序将有机会保存数据后自行退出。",
                "确认 L3", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }
        await _vm.PerformCleanupAsync(c, level);
        await _vm.RefreshAsync();
    }

    private async void L1_Click(object sender, RoutedEventArgs e) => await SelectedCleanup(CleanupActionLevel.L1Trim);
    private async void L2_Click(object sender, RoutedEventArgs e) => await SelectedCleanup(CleanupActionLevel.L2Suspend);
    private async void L3_Click(object sender, RoutedEventArgs e) => await SelectedCleanup(CleanupActionLevel.L3GracefulClose);
    private async void L4_Click(object sender, RoutedEventArgs e) => await SelectedCleanup(CleanupActionLevel.L4ForceKill);

    private async Task SelectedCleanup(CleanupActionLevel level)
    {
        var c = GetSelectedUnused();
        if (c == null)
        {
            SelectionHint.Text = "未选择任何进程 —— 不会自动回退到第一行";
            return;
        }
        await RunCleanup(c, level);
    }

    private async void CtxL1_Click(object sender, RoutedEventArgs e) { var c = GetSelectedUnused(); if (c != null) await RunCleanup(c, CleanupActionLevel.L1Trim); }
    private async void CtxL2_Click(object sender, RoutedEventArgs e) { var c = GetSelectedUnused(); if (c != null) await RunCleanup(c, CleanupActionLevel.L2Suspend); }
    private async void CtxL3_Click(object sender, RoutedEventArgs e) { var c = GetSelectedUnused(); if (c != null) await RunCleanup(c, CleanupActionLevel.L3GracefulClose); }
    private async void CtxL4_Click(object sender, RoutedEventArgs e) { var c = GetSelectedUnused(); if (c != null) await RunCleanup(c, CleanupActionLevel.L4ForceKill); }

    // ---- new primitives: resume / memory priority / eco ----

    private ProcessInfo? GetSelectedProcess() => ProcessGrid.SelectedItem as ProcessInfo;

    private void CtxResumeProcess_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) { MessageBox.Show("请先在进程列表选择一行。"); return; }
        if (!_vm.Cleanup.Suspended.IsSuspended(p.Pid)) { MessageBox.Show($"{p.Name} 不是本工具挂起的进程。"); return; }
        _vm.ResumeSuspended(new SuspendRecord { Pid = p.Pid, Name = p.Name });
    }

    private void CtxMemPriorityLow_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetMemoryPriority(p.Pid, 1); // VERY_LOW
        MessageBox.Show(ok ? $"{p.Name} 内存优先级已降为 VERY_LOW（页面优先被回收，进程不死）。" : $"设置失败（权限不足或进程已退出）。",
            "降低内存优先级", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CtxMemPriorityNormal_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetMemoryPriority(p.Pid, 5); // NORMAL
        MessageBox.Show(ok ? $"{p.Name} 内存优先级已恢复 NORMAL。" : "设置失败。", "恢复内存优先级");
    }

    private void CtxEcoOn_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetEcoMode(p.Pid, true);
        MessageBox.Show(ok ? $"{p.Name} 已开启 EcoQoS（调度器将给予更低的能效优先级）。" : "设置失败（需要 Win10 1709+ 且权限足够）。",
            "EcoQoS", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CtxEcoOff_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        _vm.ProcessService.SetEcoMode(p.Pid, false);
    }

    // ---- feedback shortcuts from the process grid ----

    private void CtxFeedbackKeep_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        SubmitFeedback(p, keep: true, clean: false, system: false);
    }

    private void CtxFeedbackClean_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        SubmitFeedback(p, keep: false, clean: true, system: false);
    }

    private void SubmitFeedback(ProcessInfo p, bool keep, bool clean, bool system)
    {
        var entry = new FeedbackEntry
        {
            ProcessName = p.Name,
            ExecutablePath = p.ExecutablePath,
            Publisher = p.SignatureSubject,
            ReportedCategory = keep ? p.Category : clean ? ProcessCategory.BackgroundService : p.Category,
            UserNote = keep ? "右键标记：需保留" : clean ? "右键标记：可清理" : "右键标记：系统进程",
            MarkKeep = keep,
            MarkCleanable = clean,
            MarkSystem = system,
            Scope = "process"
        };
        _vm.AddFeedback(entry);
        MessageBox.Show($"已记录：{p.Name} → {(keep ? "需保留" : clean ? "可清理" : "系统进程")}（下次刷新生效）。", "反馈");
    }

    // ---- suspended tab ----

    private void ResumeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SuspendedList.SelectedItem is SuspendRecord rec)
            _vm.ResumeSuspended(rec);
        else MessageBox.Show("请先选择一项。");
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e) => _vm.ResumeAllSuspended();

    private void RestartDisposal_Click(object sender, RoutedEventArgs e)
    {
        if (DisposalsGrid.SelectedItem is not DisposalRecord rec) { MessageBox.Show("请先选择一条处置记录。"); return; }
        if (MessageBox.Show($"重新启动 {rec.Name}？\n{rec.ExePath}", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (_vm.RestartDisposal(rec))
            _vm.Status = $"已重新启动 {rec.Name}";
        else
            MessageBox.Show("启动失败：exe 路径不存在或已被移动。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---- make-room workflow ----

    private async void PlanMakeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (TargetAvailGB == null) return;
        if (!double.TryParse(TargetAvailGB.Text, out var targetGB) || targetGB <= 0) { MessageBox.Show("目标可用内存请填正数（GB）。"); return; }
        await BuildAndShowPlanAsync(targetGB);
    }

    private async void HomePlan_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(HomeTargetGB.Text, out var targetGB) || targetGB <= 0) { MessageBox.Show("请填写需要的内存（GB）。"); return; }
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
            $"{i.ActionText}  {i.Process.Name} (PID {i.Process.Pid})  预计物理 {i.EstimatedPhysicalReclaimBytes / 1048576.0:F0} MB"));
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

    // ---- thread tab ----

    private void ThreadSearch_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var q = ThreadSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            _vm.RebuildThreads();
            return;
        }
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_vm.Threads);
        view.Filter = o => o is ThreadInfo t &&
            (t.OwnerPid.ToString() == q || t.Tid.ToString() == q);
    }

    private void CtxSuspendThread_Click(object sender, RoutedEventArgs e)
    {
        if (ThreadGrid.SelectedItem is ThreadInfo ti)
        {
            try { _vm.ProcessService.SuspendThread(ti.Tid); MessageBox.Show($"线程 {ti.Tid} 已挂起。", "完成"); }
            catch (Exception ex) { MessageBox.Show("挂起失败: " + ex.Message); }
        }
    }

    private void CtxResumeThread_Click(object sender, RoutedEventArgs e)
    {
        if (ThreadGrid.SelectedItem is ThreadInfo ti)
        {
            try { _vm.ProcessService.ResumeThread(ti.Tid); MessageBox.Show($"线程 {ti.Tid} 已恢复。", "完成"); }
            catch (Exception ex) { MessageBox.Show("恢复失败: " + ex.Message); }
        }
    }

    // ---- mode tab ----

    private void ApplyMode_Click(object sender, RoutedEventArgs e)
    {
        if (ModeList.SelectedItem is ListBoxItem { Tag: AppMode m })
        {
            _vm.CurrentMode = m;
            SyncModeRadios(m);
        }
    }

    private void ModeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeList.SelectedItem is ListBoxItem { Tag: AppMode m } &&
            _vm.ConfigStore.Config.ModeProfiles.TryGetValue(m, out var profile))
        {
            KeepPatternsEditor.Text = string.Join("\n", profile.KeepPatterns);
            CleanPatternsEditor.Text = string.Join("\n", profile.CleanPatterns);
            ModeEditorHint.Text = $"正在编辑: {profile.Name}";
        }
    }

    private void SaveModeRules_Click(object sender, RoutedEventArgs e)
    {
        if (ModeList.SelectedItem is not ListBoxItem { Tag: AppMode m } ||
            !_vm.ConfigStore.Config.ModeProfiles.TryGetValue(m, out var profile))
        {
            MessageBox.Show("请先在上方选择一个模式。");
            return;
        }
        profile.KeepPatterns = KeepPatternsEditor.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();
        profile.CleanPatterns = CleanPatternsEditor.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();
        _vm.ConfigStore.Save();
        _vm.Engine.SampleForeground(); // no-op; rules apply on the next engine tick
        MessageBox.Show($"已保存 {profile.Name} 的规则（下一个刷新周期生效）。", "完成");
    }

    private void SubmitFeedback_Click(object sender, RoutedEventArgs e)
    {
        var entry = new FeedbackEntry
        {
            ProcessName = FbName.Text,
            ExecutablePath = FbPath.Text,
            ReportedCategory = FbCategory.SelectedItem is ProcessCategory c ? c : ProcessCategory.Unknown,
            UserNote = FbNote.Text,
            MarkKeep = FbKeep.IsChecked == true,
            MarkCleanable = FbClean.IsChecked == true,
            MarkSystem = FbSystem.IsChecked == true,
            Scope = "process"
        };
        _vm.AddFeedback(entry);
        MessageBox.Show("反馈已记录。");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SetCpu.Text, out var cpu)) _config.Config.UnusedCpuThresholdPercent = cpu;
        if (int.TryParse(SetIdle.Text, out var idle)) _config.Config.UnusedIdleMinutes = idle;
        if (int.TryParse(SetRefresh.Text, out var rf)) _config.Config.RefreshIntervalSeconds = Math.Max(3, rf);
        if (int.TryParse(SetMinAge.Text, out var age)) _config.Config.MinProcessAgeSeconds = age;
        if (int.TryParse(SetObservations.Text, out var obs)) _config.Config.RequiredIdleObservations = Math.Max(1, obs);
        if (int.TryParse(SetGraceful.Text, out var gr)) _config.Config.GracefulCloseTimeoutSeconds = Math.Max(2, gr);
        _config.Config.OnlineLookupEndpoint = SetEndpoint.Text;
        _config.Config.OnlineLookupApiKey = SetApiKey.Text;
        _config.Config.OnlineLookupEnabled = SetOnlineEnabled.IsChecked == true;
        _config.Config.AutoCleanupEnabled = SetAutoCleanup.IsChecked == true;
        _config.Config.AutoCloseCleanMatches = SetAutoClose.IsChecked == true;
        _config.Config.AutoOptimizeEnabled = SetAutoOptimize.IsChecked == true;
        _config.Config.AutoOptimizeIntervalEnabled = SetAutoOptimizeInterval.IsChecked == true;
        _config.Config.SkipOptimizeInFullscreen = SetSkipFullscreen.IsChecked == true;
        if (AutoSwitchCheck != null) _config.Config.AutoModeSwitch = AutoSwitchCheck.IsChecked == true;
        _config.Config.StartMinimizedToTray = SetStartMinimized.IsChecked == true;
        var wantAutoStart = SetAutoStart.IsChecked == true;
        if (wantAutoStart != StartupService.IsAutoStart()) StartupService.SetAutoStart(wantAutoStart);
        _config.Config.ProtectedNames = ProtectedListEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Config.Whitelist = WhitelistEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Config.Blacklist = BlacklistEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Save();
        _vm.UnusedDetector.Config = _config.Config;
        _vm.OnlineLookup.Config = _config.Config;
        _vm.StartEngine(); // restart so the new refresh interval takes effect
        MessageBox.Show("设置已保存。", "完成");
    }

    // ---- system memory areas ----

    private void CleanMemoryAreas_Click(object sender, RoutedEventArgs e)
    {
        var areas = new List<MemoryArea>();
        if (ChkCombinedPageList.IsChecked == true) areas.Add(MemoryArea.CombinedPageList);
        if (ChkModifiedFileCache.IsChecked == true) areas.Add(MemoryArea.ModifiedFileCache);
        if (ChkModifiedPageList.IsChecked == true) areas.Add(MemoryArea.ModifiedPageList);
        if (ChkRegistryCache.IsChecked == true) areas.Add(MemoryArea.RegistryCache);
        if (ChkStandbyList.IsChecked == true) areas.Add(MemoryArea.StandbyList);
        if (ChkStandbyListLow.IsChecked == true) areas.Add(MemoryArea.StandbyListLowPriority);
        if (ChkSystemFileCache.IsChecked == true) areas.Add(MemoryArea.SystemFileCache);
        if (ChkWorkingSet.IsChecked == true) areas.Add(MemoryArea.WorkingSet);
        if (areas.Count == 0) { MessageBox.Show("请至少选择一个区域。", "提示"); return; }
        if (areas.Contains(MemoryArea.StandbyList) &&
            MessageBox.Show("完整清理备用列表会清掉文件缓存，游戏运行时会造成卡顿。确认继续？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (!_memCleaner.IsAdmin()) { MessageBox.Show("需要管理员权限。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var results = _memCleaner.CleanAreas(areas);
        var sb = new System.Text.StringBuilder();
        foreach (var kv in results) sb.AppendLine($"{kv.Key}: {(kv.Value.success ? "OK" : "FAIL")} {kv.Value.message}");
        CleanResultText.Text = sb.ToString();
        _ = _vm.RefreshAsync();
    }

    private void SelectAllAreas_Click(object sender, RoutedEventArgs e)
    {
        ChkCombinedPageList.IsChecked = true; ChkModifiedFileCache.IsChecked = true; ChkModifiedPageList.IsChecked = true;
        ChkRegistryCache.IsChecked = true; ChkStandbyList.IsChecked = true; ChkStandbyListLow.IsChecked = true;
        ChkSystemFileCache.IsChecked = true; ChkWorkingSet.IsChecked = true;
    }

    private void DeselectAllAreas_Click(object sender, RoutedEventArgs e)
    {
        ChkCombinedPageList.IsChecked = false; ChkModifiedFileCache.IsChecked = false; ChkModifiedPageList.IsChecked = false;
        ChkRegistryCache.IsChecked = false; ChkStandbyList.IsChecked = false; ChkStandbyListLow.IsChecked = false;
        ChkSystemFileCache.IsChecked = false; ChkWorkingSet.IsChecked = false;
    }

    // ---- theme ----

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        var res = Application.Current.Resources;
        if (_isDarkTheme)
        {
            res["TextPrimary"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
            res["TextSecondary"] = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            res["CardBg"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
            res["GridBg"] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            res["RowAltBg"] = new SolidColorBrush(Color.FromRgb(0x17, 0x22, 0x33));
            res["PanelBg"] = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
            res["BorderBrush"] = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
        }
        else
        {
            res["TextPrimary"] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            res["TextSecondary"] = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            res["CardBg"] = new SolidColorBrush(Colors.White);
            res["GridBg"] = new SolidColorBrush(Colors.White);
            res["RowAltBg"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB));
            res["PanelBg"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7));
            res["BorderBrush"] = new SolidColorBrush(Color.FromRgb(0xDC, 0xE1, 0xE6));
        }
    }

    // ---- tray ----

    private void UpdateTrayIcon(int usedPercent)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var color = usedPercent >= 85 ? System.Drawing.Color.FromArgb(220, 38, 38)
                          : usedPercent >= 60 ? System.Drawing.Color.FromArgb(217, 119, 6)
                          : System.Drawing.Color.FromArgb(5, 150, 105);
                using var brush = new System.Drawing.SolidBrush(color);
                g.FillEllipse(brush, 1, 1, 30, 30);
                using var font = new System.Drawing.Font("Segoe UI", usedPercent >= 100 ? 10f : 12f, System.Drawing.FontStyle.Bold);
                var text = usedPercent.ToString();
                var size = g.MeasureString(text, font);
                using var white = System.Drawing.Brushes.White;
                g.DrawString(text, font, white, (32 - size.Width) / 2f, (32 - size.Height) / 2f + 1f);
            }
            var hicon = bmp.GetHicon();
            var newIcon = System.Drawing.Icon.FromHandle(hicon);
            _trayIcon!.Icon = newIcon;
            // free the previous icon handle (Icon.FromHandle does not take ownership)
            if (_trayIconObj != null) { _trayIconObj.Dispose(); }
            if (_trayHicon != IntPtr.Zero) { DestroyIcon(_trayHicon); }
            _trayIconObj = newIcon;
            _trayHicon = hicon;
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void StartTrayUpdateTimer()
    {
        _trayUpdateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trayUpdateTimer.Tick += (s, e) =>
        {
            if (_trayIcon == null) return;
            if (_vm.ConfigStore.Config.ShowMemoryInTray && _vm.SysMem.TotalPhysicalBytes > 0)
            {
                _trayIcon.Text = $"MemoryTool - 已用 {_vm.SysMem.MemoryLoadPercent:F0}%，可用 {_vm.AvailableMemoryText}，挂起 {_vm.SuspendedProcesses.Count}";
                UpdateTrayIcon((int)Math.Round(_vm.SysMem.MemoryLoadPercent));
            }
            else
            {
                _trayIcon.Text = "MemoryTool";
            }
        };
        _trayUpdateTimer.Start();
    }
}
