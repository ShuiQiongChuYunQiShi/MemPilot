using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;
using MemPilot.ViewModels;
using Application = System.Windows.Application;
using TabControl = System.Windows.Controls.TabControl;
using RadioButton = System.Windows.Controls.RadioButton;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace MemPilot.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ConfigStore _config;
    private readonly NativeMemoryCleanerService _memCleaner = new();
    private TrayIconService? _tray;
    private HotkeyService? _hotkey;
    private CompactWindow? _compact;
    private IntPtr _trayHicon = IntPtr.Zero;
    private System.Windows.Controls.ContextMenu? _trayMenu;
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
        // 告警触达：引擎的 Alert 事件 → 托盘气泡（主页横幅由 VM 内部处理）
        _vm.AlertRaised += (title, message, warning) =>
            Dispatcher.InvokeAsync(() => _tray?.ShowBalloon(title, message, warning));

        SyncModeRadios(_vm.CurrentMode);

        // per-tab controls are initialized lazily when their tab is first shown
        // (TabControl content is instantiated on selection)

        _hotkey = new HotkeyService();
        _hotkey.Activated += () => Dispatcher.InvokeAsync(HotkeyActivateMakeRoom);
        if (_config.Config.GlobalHotkeyEnabled)
            _hotkey.TryRegister(_config.Config.GlobalHotkey);

        _tray = new TrayIconService();
        _tray.AddBlocked += n => Dispatcher.InvokeAsync(() =>
        {
            _tray?.ShowBalloon("托盘图标被安全软件拦截",
                "安全软件（如 360）拒绝了托盘图标注册。请把 MemPilot 加入其信任区；加入后几秒内图标会自动出现，无需重启程序。", warning: true);
            _vm.Status = "托盘图标注册被安全软件拒绝——请将 MemPilot 加入信任区，加入后图标会自动出现。";
        });
        _tray.DoubleClick += () => Dispatcher.InvokeAsync(ShowWindow);
        _tray.RightClick += () => Dispatcher.InvokeAsync(ShowTrayMenu);
        _tray.SetTooltip("MemPilot");
        _trayMenu = new System.Windows.Controls.ContextMenu
        {
            Items =
            {
                new System.Windows.Controls.MenuItem { Header = "显示" },
                new System.Windows.Controls.MenuItem { Header = "刷新" },
                new System.Windows.Controls.MenuItem { Header = "退出" }
            }
        };
        var miShow = (System.Windows.Controls.MenuItem)_trayMenu.Items[0];
        var miRefresh = (System.Windows.Controls.MenuItem)_trayMenu.Items[1];
        var miExit = (System.Windows.Controls.MenuItem)_trayMenu.Items[2];
        miShow.Click += (s, e) => ShowWindow();
        miRefresh.Click += async (s, e) => await _vm.RefreshAsync();
        miExit.Click += (s, e) => { _allowClose = true; _tray?.Dispose(); Application.Current.Shutdown(); };

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
            Dispatcher.InvokeAsync(() => _tray?.ShowBalloon("模式已切换",
                $"已自动切换到 {e.Mode}（{e.Reason}），详见决策日志。"));

        App.ShowRequested += () => Dispatcher.InvokeAsync(ShowWindow);
        Closing += MainWindow_Closing;
        _vm.CollectionsRebuilt += (s, e) => RestoreSelection();
        Loaded += async (s, e) =>
        {
            SyncModeRadios(_vm.CurrentMode);
            if (AutoSwitchCheck != null) AutoSwitchCheck.IsChecked = _config.Config.AutoModeSwitch;
            UpdateUiActivity(); // start-minimized sessions begin in lightweight mode
            await _vm.RefreshAsync();
            _vm.StartEngine();          // resident engine: periodic refresh + policies
            StartTrayUpdateTimer();
        };
        StateChanged += (s, e) => UpdateUiActivity();
        IsVisibleChanged += (s, e) => UpdateUiActivity();
    }

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

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (_compact != null) { _compact.Activate(); return; }
        _compact = new CompactWindow(_vm, _config, OnCompactMakeRoom, ShowWindow);
        _compact.Closed += (s, e) => _compact = null;
        _compact.Show();
        Hide(); // the mini bar replaces the main window; tray stays alive
        UpdateUiActivity();
    }

    private void OnCompactMakeRoom()
    {
        ShowWindow();
        SwitchToTab("🚀");
        _ = BuildMakeRoomFromHomeAsync();
    }

    private void LoadHistory_Click(object sender, RoutedEventArgs e) => _vm.LoadHistory();

    private void ShowTrayMenu()
    {
        if (_trayMenu == null) return;
        _trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _trayMenu.IsOpen = true;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        UpdateUiActivity();
    }

    /// <summary>Lightweight mode: while the window is hidden/minimized the refresh loop runs
    /// at a slower cadence and skips rebuilding UI collections — the engine keeps deciding,
    /// only the tray numbers stay fresh. A memory manager must not be the memory hog.</summary>
    private DateTime _lastSelfTrimUtc = DateTime.MinValue;
    private int _promoteAttempts;

    /// <summary>Windows 11 defaults new tray icons into the hidden overflow ("^"). Promote
    /// ourselves in the shell's NotifyIconSettings so the icon is always visible in the
    /// main tray area — the programmatic equivalent of dragging it out once.</summary>
    private void PromoteTrayIcon()
    {
        if (_promoteAttempts > 100) return;
        _promoteAttempts++;
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true);
            if (root == null) return;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, true);
                if (k == null) continue;
                if (k.GetValue("ExecutablePath") is string path &&
                    string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                {
                    var current = k.GetValue("IsPromoted");
                    if (current is int i && i == 1) { _promoteAttempts = int.MaxValue - 1; return; }
                    k.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    _vm.Status = "托盘图标已设为常驻显示。";
                    return;
                }
            }
        }
        catch { }
    }

    private void UpdateUiActivity()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized;
        _vm.UiActive = active;
        int baseSec = Math.Max(3, _config.Config.RefreshIntervalSeconds);
        _vm.Engine.SetIntervalSeconds(active ? baseSec : Math.Max(30, baseSec * 3));
        if (!active) ScheduleSelfTrim(TimeSpan.FromSeconds(3));
    }

    /// <summary>Eat our own dog food: in tray mode, trim our working set so the memory
    /// manager is not itself the memory hog. Pages go to standby and return on demand.</summary>
    private void ScheduleSelfTrim(TimeSpan delay)
    {
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (!_vm.UiActive)
            {
                _vm.ProcessService.TrimWorkingSet(Environment.ProcessId);
                _lastSelfTrimUtc = DateTime.UtcNow;
            }
        });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            UpdateUiActivity(); // enter tray lightweight mode
            return;
        }
        _tray?.Dispose();
        _hotkey?.Dispose();
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

    private void ProcessGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is not FrameworkElement) return;
        var p = (sender as System.Windows.Controls.DataGrid)?.SelectedItem as ProcessInfo;
        if (p == null || string.IsNullOrEmpty(p.ExecutablePath) || !System.IO.File.Exists(p.ExecutablePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{p.ExecutablePath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void TreeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null || TreeModeToggle == null) return;
        _vm.TreeMode = TreeModeToggle.IsChecked == true;
    }

    private void TreeToggle_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProcessInfo p) _vm.ToggleCollapse(p.Pid);
    }

    private void ProcessGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // column sorting would scramble the tree order; tree mode keeps its own ordering
        if (_vm.TreeMode) e.Handled = true;
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
            _vm.Status = "当前没有可清理候选（观察期可能未满，工具刚启动时为空是正常的）。";
            return;
        }
        long before = _vm.SysMem.AvailablePhysicalBytes;
        foreach (var c in targets)
            await _vm.PerformCleanupAsync(c, CleanupActionLevel.L1Trim);
        await _vm.RefreshAsync();
        long delta = _vm.SysMem.AvailablePhysicalBytes - before;
        string msg = $"已回收 {targets.Count} 项工作集，可用内存 {(delta >= 0 ? "+" : "")}{delta / 1073741824.0:F1} GB";
        _tray?.ShowBalloon("一键优化完成", msg);
        _vm.Status = msg;
    }

    private void ToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        UpdateUiActivity();
    }

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

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (SetAutoStart == null) return;
        bool want = SetAutoStart.IsChecked == true;
        StartupService.SetAutoStart(want); // applies immediately, not on "save"
        if (_config != null)
        {
            _config.Config.StartWithWindows = want;
            _config.Save();
        }
        _vm.Status = want ? "已开启开机自启。" : "已关闭开机自启。";
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (_config == null || SetStartMinimized == null) return;
        _config.Config.StartMinimizedToTray = SetStartMinimized.IsChecked == true;
        _config.Save();
    }

    private void ResumeOnChange_Changed(object sender, RoutedEventArgs e)
    {
        if (_config == null || SetResumeOnModeChange == null) return;
        _config.Config.ResumeSuspendedOnModeChange = SetResumeOnModeChange.IsChecked == true;
        _config.Save();
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
        // 页签感知：VM 只重建当前可见页签的集合（性能优化，见 UpdateCollections 注释）
        var key = header.StartsWith("🏠") ? "🏠" : header.StartsWith("📋") ? "📋" : header.StartsWith("🧹") ? "🧹"
                : header.StartsWith("⏸") ? "⏸" : header.StartsWith("🧠") ? "🧠" : "other";
        _vm.OnTabActivated(key);
        if (header.StartsWith("🧵")) _vm.RebuildThreads();
        else if (header.StartsWith("🎚")) InitModeTab();
        else if (header.StartsWith("💬")) InitFeedbackTab();
        else if (header.StartsWith("⚙")) InitSettingsTab();
        else if (header.StartsWith("📊")) _vm.LoadHistory();
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
        SetAutoJunkSweep.IsChecked = _config.Config.AutoJunkSweepEnabled;
        SetAutoJunkSweepOrphansOnly.IsChecked = _config.Config.AutoJunkSweepOrphansOnly;
        SetShowAlerts.IsChecked = _config.Config.ShowAlerts;
        SetSkipFullscreen.IsChecked = _config.Config.SkipOptimizeInFullscreen;
        SetAutoStart.IsChecked = StartupService.IsAutoStart();
        SetStartMinimized.IsChecked = _config.Config.StartMinimizedToTray;
        SetHotkeyEnabled.IsChecked = _config.Config.GlobalHotkeyEnabled;
        SetHotkey.Text = _config.Config.GlobalHotkey;
        SetEventLog.IsChecked = _config.Config.WriteEventLogAudit;
        SetResumeOnModeChange.IsChecked = _config.Config.ResumeSuspendedOnModeChange;
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
        var childNote = c.Process.ChildPids.Count > 0
            ? $"\n注意：该进程有 {c.Process.ChildPids.Count} 个子进程，结束可能连带影响它们。"
            : "";
        if (level == CleanupActionLevel.L4ForceKill)
        {
            var msg = $"强制结束 {c.Process.Name} (PID {c.Process.Pid})？\n预计释放: {c.EstimatedReclaimableBytes / 1048576.0:F0} MB{childNote}\n该操作不可撤销。";
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
            if (MessageBox.Show($"向 {c.Process.Name} (PID {c.Process.Pid}) 的窗口发送关闭消息？程序将有机会保存数据后自行退出。{childNote}",
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

    // ---- 告警触达（v0.1.8）：主页横幅 + 托盘气泡 ----

    private async void QuickRelief_Click(object sender, RoutedEventArgs e)
    {
        _vm.Status = "正在低风险腾挪（压缩工作集 + 清孤儿残留）…";
        try
        {
            var msg = await _vm.QuickReliefAsync();
            _vm.Status = msg;
        }
        catch (Exception ex) { _vm.Status = "腾挪失败: " + ex.Message; }
    }

    private void DismissAlert_Click(object sender, RoutedEventArgs e) => _vm.DismissAlert();

    private void ApplyLowRiskPreset_Click(object sender, RoutedEventArgs e)
    {
        SetAutoOptimize.IsChecked = true;
        SetAutoJunkSweep.IsChecked = true;
        SetAutoJunkSweepOrphansOnly.IsChecked = true;
        SaveSettings_Click(sender, e); // 复用保存路径：落盘并重启引擎
        _vm.Status = "已应用低风险自动档：自动优化 + 仅清孤儿残留（清理仍只 L1，不会结束进程）。";
    }

    // ---- 未识别复核队列（P3.3）：人工核实后写入反馈库，分类器下一轮生效 ----

    private ProcessInfo? GetSelectedReview() => ReviewGrid?.SelectedItem as ProcessInfo;

    private async void ReviewMarkClean_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedReview();
        if (p == null) { SelectionHint.Text = "请先在复核队列里选择一行"; return; }
        _vm.Feedback.Add(new FeedbackEntry
        {
            ProcessName = p.Name,
            ExecutablePath = p.ExecutablePath,
            ReportedCategory = ProcessCategory.Bloatware,
            MarkCleanable = true
        });
        SelectionHint.Text = $"已记录：{p.Name} 标记为可清理（下次刷新生效，可在反馈页撤销）";
        await _vm.RefreshAsync();
    }

    private async void ReviewMarkKeep_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedReview();
        if (p == null) { SelectionHint.Text = "请先在复核队列里选择一行"; return; }
        _vm.Feedback.Add(new FeedbackEntry
        {
            ProcessName = p.Name,
            ExecutablePath = p.ExecutablePath,
            ReportedCategory = ProcessCategory.ThirdPartyApp,
            MarkKeep = true
        });
        SelectionHint.Text = $"已记录：{p.Name} 标记为保留（白名单，后续不再提示）";
        await _vm.RefreshAsync();
    }

    private async void ReviewLookup_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedReview();
        if (p == null) { SelectionHint.Text = "请先在复核队列里选择一行"; return; }
        SelectionHint.Text = $"正在联网核实 {p.Name} …";
        try
        {
            var r = await _vm.OnlineLookup.LookupAsync(p);
            _vm.Classification.ApplyOnlineResult(p, r);
            SelectionHint.Text = $"核实结果（{p.Name}）：{r.Status} — {r.Summary}";
            await _vm.RefreshAsync();
        }
        catch (Exception ex) { SelectionHint.Text = "核实失败: " + ex.Message; }
    }

    // ---- 批量处置（一键压缩 / 全部候选 L1 / 全部候选按建议级别） ----

    private async void SweepJunk_Click(object sender, RoutedEventArgs e)
    {
        var plan = _vm.BuildJunkSweepPlan();
        if (plan.Count == 0) { SelectionHint.Text = "没有发现可清扫的垃圾驻守进程"; return; }
        int kills = plan.Count(i => i.Action == CleanupActionLevel.L4ForceKill);
        int trims = plan.Count - kills;
        var names = string.Join("、", plan.Take(10).Select(i => $"{i.Process.Name}(PID {i.Process.Pid})"));
        var more = plan.Count > 10 ? $" 等 {plan.Count} 个" : "";
        var msg = $"垃圾驻守清扫计划：\n  直接结束 {kills} 个、回收工作集 {trims} 个\n\n{names}{more}\n\n继续？";
        if (MessageBox.Show(msg, "确认清扫", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SelectionHint.Text = "正在清扫…";
        var summary = await _vm.ExecuteJunkSweepAsync(plan);
        SelectionHint.Text = summary;
        await _vm.RefreshAsync();
    }

    private async void TrimAllWs_Click(object sender, RoutedEventArgs e)
    {
        SelectionHint.Text = "正在压缩全部可触及进程的工作集…";
        try
        {
            var msg = await _vm.TrimAllWorkingSetsAsync();
            SelectionHint.Text = msg;
            if (CleanResultText != null) CleanResultText.Text = msg; // 系统内存清理页也显示结果
            await _vm.RefreshAsync();
        }
        catch (Exception ex) { SelectionHint.Text = "压缩失败: " + ex.Message; }
    }

    private async void BatchL1_Click(object sender, RoutedEventArgs e)
    {
        var cands = _vm.UnusedCandidates.ToList();
        if (cands.Count == 0) { SelectionHint.Text = "当前没有可清理候选"; return; }
        SelectionHint.Text = $"正在回收 {cands.Count} 个候选的工作集…";
        var (_, _, summary) = await _vm.BatchCleanupAsync(cands, CleanupActionLevel.L1Trim);
        SelectionHint.Text = summary;
        await _vm.RefreshAsync();
    }

    private async void BatchSuggested_Click(object sender, RoutedEventArgs e)
    {
        var cands = _vm.UnusedCandidates.ToList();
        if (cands.Count == 0) { SelectionHint.Text = "当前没有可清理候选"; return; }
        int l4 = cands.Count(c => c.SuggestedLevel == CleanupActionLevel.L4ForceKill);
        int l3 = cands.Count(c => c.SuggestedLevel == CleanupActionLevel.L3GracefulClose);
        int l2 = cands.Count(c => c.SuggestedLevel == CleanupActionLevel.L2Suspend);
        if (l3 + l4 + l2 > 0)
        {
            var destructive = cands.Where(c => c.SuggestedLevel >= CleanupActionLevel.L3GracefulClose)
                                   .Select(c => $"{c.Process.Name}(PID {c.Process.Pid})").ToList();
            var names = string.Join("、", destructive.Take(8)) + (destructive.Count > 8 ? $" 等 {destructive.Count} 个" : "");
            var msg = $"将批量处置 {cands.Count} 个候选：优雅关闭 {l3} 个、强制结束 {l4} 个、挂起 {l2} 个。\n涉及：{names}\n\n继续？";
            if (MessageBox.Show(msg, "确认批量清理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        SelectionHint.Text = $"正在批量处置 {cands.Count} 个候选…";
        var (_, _, summary) = await _vm.BatchCleanupAsync(cands);
        SelectionHint.Text = summary;
        await _vm.RefreshAsync();
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
        if (p == null) { _vm.Status = "请先在进程列表选择一行。"; return; }
        if (!_vm.Cleanup.Suspended.IsSuspended(p.Pid)) { _vm.Status = $"{p.Name} 不是本工具挂起的进程。"; return; }
        _vm.ResumeSuspended(new SuspendRecord { Pid = p.Pid, Name = p.Name });
    }

    private void CtxMemPriorityLow_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetMemoryPriority(p.Pid, 1); // VERY_LOW
        _vm.Status = ok ? $"{p.Name} 内存优先级已降为 VERY_LOW（页面优先被回收，进程不死）。" : $"内存优先级设置失败（权限不足或进程已退出）。";
    }

    private void CtxMemPriorityNormal_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetMemoryPriority(p.Pid, 5); // NORMAL
        _vm.Status = ok ? $"{p.Name} 内存优先级已恢复 NORMAL。" : "内存优先级设置失败。";
    }

    private void CtxEcoOn_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetEcoMode(p.Pid, true);
        _vm.Status = ok ? $"{p.Name} 已开启 EcoQoS（调度器将给予更低的能效优先级）。" : "EcoQoS 设置失败（需要 Win10 1709+ 且权限足够）。";
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
        _vm.Status = $"已记录：{p.Name} → {(keep ? "需保留" : clean ? "可清理" : "系统进程")}（下次刷新生效）。";
    }

    // ---- suspended tab ----

    private void ResumeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SuspendedList.SelectedItem is SuspendRecord rec)
            _vm.ResumeSuspended(rec);
        else _vm.Status = "请先选择一项挂起记录。";
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e) => _vm.ResumeAllSuspended();

    private void RestartDisposal_Click(object sender, RoutedEventArgs e)
    {
        if (DisposalsGrid == null || DisposalsGrid.SelectedItem is not DisposalRecord rec) { _vm.Status = "请先选择一条处置记录。"; return; }
        if (MessageBox.Show($"重新启动 {rec.Name}？\n{rec.ExePath}", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (_vm.RestartDisposal(rec))
            _vm.Status = $"已重新启动 {rec.Name}";
        else
        {
            _vm.Status = "启动失败：exe 路径不存在或已被移动。";
            _tray?.ShowBalloon("重启失败", "exe 路径不存在或已被移动。", warning: true);
        }
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
            try { _vm.ProcessService.SuspendThread(ti.Tid); _vm.Status = $"线程 {ti.Tid} 已挂起。"; }
            catch (Exception ex) { _vm.Status = "线程挂起失败: " + ex.Message; }
        }
    }

    private void CtxResumeThread_Click(object sender, RoutedEventArgs e)
    {
        if (ThreadGrid.SelectedItem is ThreadInfo ti)
        {
            try { _vm.ProcessService.ResumeThread(ti.Tid); _vm.Status = $"线程 {ti.Tid} 已恢复。"; }
            catch (Exception ex) { _vm.Status = "线程恢复失败: " + ex.Message; }
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
            _vm.Status = "请先在上方选择一个模式。";
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
        _config.Config.AutoJunkSweepEnabled = SetAutoJunkSweep.IsChecked == true;
        _config.Config.AutoJunkSweepOrphansOnly = SetAutoJunkSweepOrphansOnly.IsChecked == true;
        _config.Config.ShowAlerts = SetShowAlerts.IsChecked == true;
        _config.Config.SkipOptimizeInFullscreen = SetSkipFullscreen.IsChecked == true;
        if (AutoSwitchCheck != null) _config.Config.AutoModeSwitch = AutoSwitchCheck.IsChecked == true;
        _config.Config.StartMinimizedToTray = SetStartMinimized.IsChecked == true;
        _config.Config.WriteEventLogAudit = SetEventLog.IsChecked == true;
        _config.Config.ResumeSuspendedOnModeChange = SetResumeOnModeChange.IsChecked == true;
        WindowsEventLog.Enabled = _config.Config.WriteEventLogAudit;
        _config.Config.GlobalHotkeyEnabled = SetHotkeyEnabled.IsChecked == true;
        _config.Config.GlobalHotkey = SetHotkey.Text.Trim().ToUpperInvariant();
        var wantAutoStart = SetAutoStart.IsChecked == true;
        if (wantAutoStart != StartupService.IsAutoStart()) StartupService.SetAutoStart(wantAutoStart);
        _config.Config.ProtectedNames = ProtectedListEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Config.Whitelist = WhitelistEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Config.Blacklist = BlacklistEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Save();
        _vm.UnusedDetector.Config = _config.Config;
        _vm.OnlineLookup.Config = _config.Config;
        _vm.StartEngine(); // restart so the new refresh interval takes effect
        UpdateUiActivity();
        _hotkey?.Unregister();
        string hotkeyNote = "";
        if (_config.Config.GlobalHotkeyEnabled)
        {
            if (_hotkey != null && _hotkey.TryRegister(_config.Config.GlobalHotkey))
                hotkeyNote = " 全局热键已生效。";
            else
                hotkeyNote = " 全局热键注册失败（格式如 CTRL+SHIFT+M，或被其他程序占用）。";
        }
        _vm.Status = "设置已保存。" + hotkeyNote;
        _tray?.ShowBalloon("设置已保存", string.IsNullOrWhiteSpace(hotkeyNote) ? "配置已更新。" : hotkeyNote.Trim(), warning: hotkeyNote.Contains("失败"));
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
        if (!Elevation.IsAdmin())
        {
            var argList = areas.Select(a => "/" + a).ToList();
            var res2 = MessageBox.Show(
                "系统级内存清理需要管理员权限。\n\n以管理员身份重启 MemPilot 并自动执行所选区域？",
                "需要提权", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res2 == MessageBoxResult.Yes &&
                Elevation.RestartAsAdmin(string.Join(" ", argList)))
            {
                _allowClose = true;
                _tray?.Dispose();
                _hotkey?.Dispose();
                Application.Current.Shutdown();
            }
            return;
        }
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
        void Set(string key, byte r, byte g, byte b) => res[key] = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (_isDarkTheme)
        {
            Set("TextPrimary", 0xE5, 0xE7, 0xEB); Set("TextSecondary", 0x9C, 0xA3, 0xAF);
            Set("CardBg", 0x1E, 0x29, 0x3B); Set("GridBg", 0x11, 0x18, 0x27);
            Set("RowAltBg", 0x17, 0x22, 0x33); Set("PanelBg", 0x0F, 0x17, 0x2A);
            Set("BorderBrush", 0x33, 0x41, 0x55);
            Set("InputBg", 0x1E, 0x29, 0x3B); Set("ControlBorder", 0x33, 0x41, 0x55);
            Set("ControlFg", 0xE5, 0xE7, 0xEB); Set("TrackBg", 0x1E, 0x29, 0x3B);
            Set("ChipBg", 0x26, 0x33, 0x4A); Set("ChipHoverBg", 0x33, 0x41, 0x55);
            Set("SystemRowBg", 0x16, 0x23, 0x3B); Set("GameRowBg", 0x12, 0x29, 0x1E);
            Set("AiRowBg", 0x23, 0x16, 0x33); Set("UnknownRowBg", 0x2B, 0x24, 0x15);
        }
        else
        {
            Set("TextPrimary", 0x11, 0x18, 0x27); Set("TextSecondary", 0x6B, 0x72, 0x80);
            Set("CardBg", 0xFF, 0xFF, 0xFF); Set("GridBg", 0xFF, 0xFF, 0xFF);
            Set("RowAltBg", 0xF9, 0xFA, 0xFB); Set("PanelBg", 0xF2, 0xF4, 0xF7);
            Set("BorderBrush", 0xDC, 0xE1, 0xE6);
            Set("InputBg", 0xFF, 0xFF, 0xFF); Set("ControlBorder", 0xCB, 0xD5, 0xE1);
            Set("ControlFg", 0x11, 0x18, 0x27); Set("TrackBg", 0xE2, 0xE8, 0xF0);
            Set("ChipBg", 0xF1, 0xF5, 0xF9); Set("ChipHoverBg", 0xE2, 0xE8, 0xF0);
            Set("SystemRowBg", 0xE8, 0xF0, 0xFE); Set("GameRowBg", 0xE7, 0xF8, 0xEE);
            Set("AiRowBg", 0xF3, 0xE8, 0xFF); Set("UnknownRowBg", 0xFF, 0xF4, 0xE5);
        }
    }

    // ---- tray ----

    private void UpdateTrayIcon(int usedPercent)
    {
        try
        {
            // draw at the shell's native small-icon size so it stays crisp at any DPI
            int px = Math.Max(16, GetSystemMetrics(49)); // SM_CXSMICON, DPI-aware shell size
            using var bmp = new System.Drawing.Bitmap(px, px);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                float inset = px * 0.12f;
                float thick = px * 0.17f;
                var rect = new System.Drawing.RectangleF(inset, inset, px - 2 * inset, px - 2 * inset);

                // dim track ring
                using (var track = new System.Drawing.Pen(System.Drawing.Color.FromArgb(70, 90, 110), thick))
                    g.DrawArc(track, rect, -90, 360);

                // value arc, colored by pressure
                var color = usedPercent >= 85 ? System.Drawing.Color.FromArgb(235, 61, 61)
                          : usedPercent >= 60 ? System.Drawing.Color.FromArgb(217, 119, 6)
                          : System.Drawing.Color.FromArgb(16, 185, 129);
                using (var pen = new System.Drawing.Pen(color, thick))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawArc(pen, rect, -90, 360f * Math.Clamp(usedPercent, 0, 100) / 100f);
                }

                // percentage text only when there is room to render it legibly
                if (px >= 28)
                {
                    using var font = new System.Drawing.Font("Segoe UI", px * 0.30f, System.Drawing.FontStyle.Bold);
                    using var white = System.Drawing.Brushes.White;
                    var text = usedPercent.ToString();
                    var size = g.MeasureString(text, font);
                    g.DrawString(text, font, white, (px - size.Width) / 2f, (px - size.Height) / 2f + 1f);
                }
            }
            var hicon = bmp.GetHicon();
            _tray?.SetIcon(hicon);
            if (_trayHicon != IntPtr.Zero) { DestroyIcon(_trayHicon); }
            _trayHicon = hicon;
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private void StartTrayUpdateTimer()
    {
        _trayUpdateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trayUpdateTimer.Tick += (s, e) =>
        {
            if (_tray == null) return;
            if (_promoteAttempts <= 100 && Environment.TickCount64 % 12000 < 3000) PromoteTrayIcon();
            if (!_vm.UiActive && (DateTime.UtcNow - _lastSelfTrimUtc).TotalSeconds > 60)
            {
                _lastSelfTrimUtc = DateTime.UtcNow;
                _vm.ProcessService.TrimWorkingSet(Environment.ProcessId);
            }
            if (_vm.ConfigStore.Config.ShowMemoryInTray && _vm.SysMem.TotalPhysicalBytes > 0)
            {
                _tray.SetTooltip($"MemPilot - 已用 {_vm.SysMem.MemoryLoadPercent:F0}%，可用 {_vm.AvailableMemoryText}，挂起 {_vm.SuspendedProcesses.Count}");
                UpdateTrayIcon((int)Math.Round(_vm.SysMem.MemoryLoadPercent));
            }
            else
            {
                _tray.SetTooltip("MemPilot");
            }
        };
        _trayUpdateTimer.Start();
    }
}
