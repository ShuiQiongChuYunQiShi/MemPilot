using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using MemoryTool.Models;
using MemoryTool.Services;
using MemoryTool.ViewModels;
using Application = System.Windows.Application;

namespace MemoryTool.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ConfigStore _config;
    private readonly AuditLogger _audit;
    private readonly FeedbackStore _feedback;
    private readonly NativeMemoryCleanerService _memCleaner = new();
    private NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _isDarkTheme = false;
    private System.Windows.Threading.DispatcherTimer? _autoModeTimer;
    private System.Windows.Threading.DispatcherTimer? _autoOptimizeTimer;
    private System.Windows.Threading.DispatcherTimer? _trayUpdateTimer;
    private DateTime _lastAutoOptimize = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        var dataDir = ResolveDataDir();
        _config = new ConfigStore(dataDir);
        _audit = new AuditLogger(dataDir);
        _feedback = new FeedbackStore(dataDir);
        _vm = new MainViewModel(_config, _audit, _feedback);
        DataContext = _vm;

        foreach (AppMode m in Enum.GetValues(typeof(AppMode)))
            ModeCombo.Items.Add(m);
        ModeCombo.SelectedItem = _vm.CurrentMode;

        foreach (ProcessCategory c in Enum.GetValues(typeof(ProcessCategory)))
            FbCategory.Items.Add(c);
        FbCategory.SelectedItem = ProcessCategory.ThirdPartyApp;

        SetCpu.Text = _config.Config.UnusedCpuThresholdPercent.ToString();
        SetIdle.Text = _config.Config.UnusedIdleMinutes.ToString();
        SetRefresh.Text = _config.Config.RefreshIntervalSeconds.ToString();
        SetEndpoint.Text = _config.Config.OnlineLookupEndpoint;
        SetApiKey.Text = _config.Config.OnlineLookupApiKey;
        SetOnlineEnabled.IsChecked = _config.Config.OnlineLookupEnabled;
        AutoSwitchCheck.IsChecked = _config.Config.AutoModeSwitch;
        ProtectedListEditor.Text = string.Join("\n", _config.Config.ProtectedNames);
        WhitelistEditor.Text = string.Join("\n", _config.Config.Whitelist);
        BlacklistEditor.Text = string.Join("\n", _config.Config.Blacklist);

        foreach (var kv in _config.Config.ModeProfiles)
            ModeList.Items.Add(kv.Value.Name + " (" + kv.Key + ")");

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MemoryTool",
            Visible = true
        };
        _trayIcon.DoubleClick += (s, e) => ShowWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (s, e) => ShowWindow());
        menu.Items.Add("Refresh", null, async (s, e) => await _vm.RefreshAsync());
        menu.Items.Add("Exit", null, (s, e) => { _allowClose = true; Application.Current.Shutdown(); });
        _trayIcon.ContextMenuStrip = menu;

        Closing += MainWindow_Closing;
        Loaded += async (s, e) => { await _vm.RefreshAsync(); StartAutoModeTimer(); StartAutoOptimizeTimer(); StartTrayUpdateTimer(); };
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _vm.UnusedCandidates.ToList())
        {
            await _vm.PerformCleanupAsync(c, CleanupActionLevel.L1Trim);
        }
        await _vm.RefreshAsync();
    }

    private void ToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeCombo.SelectedItem is AppMode m)
            _vm.CurrentMode = m;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.SearchText = SearchBox.Text;
    }

    private async void L1_Click(object sender, RoutedEventArgs e)
    {
        var c = GetSelectedUnused();
        if (c != null) { await _vm.PerformCleanupAsync(c, CleanupActionLevel.L1Trim); await _vm.RefreshAsync(); }
    }
    private async void L2_Click(object sender, RoutedEventArgs e)
    {
        var c = GetSelectedUnused();
        if (c != null)
        {
            var res = System.Windows.MessageBox.Show($"Suspend {c.Process.Name}? ", "Confirm", MessageBoxButton.YesNo);
            if (res == MessageBoxResult.Yes)
            {
                await _vm.PerformCleanupAsync(c, CleanupActionLevel.L2Suspend);
                await _vm.RefreshAsync();
            }
        }
    }
    private async void L4_Click(object sender, RoutedEventArgs e)
    {
        var c = GetSelectedUnused();
        if (c != null)
        {
            var msg = $"Force kill {c.Process.Name} (PID {c.Process.Pid})?\nEst. reclaim: {c.EstimatedReclaimableBytes} bytes.\nThis cannot be undone.";
            var res = System.Windows.MessageBox.Show(msg, "Confirm L4", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                var res2 = System.Windows.MessageBox.Show("Second confirmation: proceed with force kill?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res2 == MessageBoxResult.Yes)
                {
                    await _vm.PerformCleanupAsync(c, CleanupActionLevel.L4ForceKill);
                    await _vm.RefreshAsync();
                }
            }
        }
    }

    private void CtxL1_Click(object sender, RoutedEventArgs e)
    {
        var c = GetContextCandidate(sender);
        if (c != null) { _ = RunCleanup(c, CleanupActionLevel.L1Trim); }
    }
    private void CtxL2_Click(object sender, RoutedEventArgs e)
    {
        var c = GetContextCandidate(sender);
        if (c != null) { _ = RunCleanup(c, CleanupActionLevel.L2Suspend); }
    }
    private void CtxL4_Click(object sender, RoutedEventArgs e)
    {
        var c = GetContextCandidate(sender);
        if (c != null) { _ = RunCleanup(c, CleanupActionLevel.L4ForceKill); }
    }
    private UnusedCandidate? GetContextCandidate(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is DataGrid dg)
        {
            if (dg.SelectedItem is UnusedCandidate uc) return uc;
            if (dg.SelectedItem is ProcessInfo pi)
            {
                return new UnusedCandidate { Process = pi, Reasons = new List<string> { "context menu" }, EstimatedReclaimableBytes = (long)(pi.Memory.WorkingSetBytes * 0.7), Risk = ProcessRiskLevel.Medium, SuggestedLevel = CleanupActionLevel.L1Trim };
            }
        }
        return GetSelectedUnused();
    }
    private async Task RunCleanup(UnusedCandidate c, CleanupActionLevel level)
    {
        if (level == CleanupActionLevel.L4ForceKill)
        {
            var msg = $"Force kill {c.Process.Name} (PID {c.Process.Pid})?\nEst. reclaim: {c.EstimatedReclaimableBytes} bytes.\nThis cannot be undone.";
            if (System.Windows.MessageBox.Show(msg, "Confirm L4", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (System.Windows.MessageBox.Show("Second confirmation: proceed with force kill?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        else if (level == CleanupActionLevel.L2Suspend)
        {
            if (System.Windows.MessageBox.Show($"Suspend {c.Process.Name}? ", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        }
        await _vm.PerformCleanupAsync(c, level);
        await _vm.RefreshAsync();
    }

    private void CtxSuspendThread_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is DataGrid dg && dg.SelectedItem is ThreadInfo ti)
        {
            try { _vm.ProcessService.SuspendThread(ti.Tid); System.Windows.MessageBox.Show($"Thread {ti.Tid} suspended.", "Done"); }
            catch (Exception ex) { System.Windows.MessageBox.Show("Suspend failed: " + ex.Message); }
        }
    }
    private void CtxResumeThread_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is DataGrid dg && dg.SelectedItem is ThreadInfo ti)
        {
            try { _vm.ProcessService.ResumeThread(ti.Tid); System.Windows.MessageBox.Show($"Thread {ti.Tid} resumed.", "Done"); }
            catch (Exception ex) { System.Windows.MessageBox.Show("Resume failed: " + ex.Message); }
        }
    }

    private UnusedCandidate? GetSelectedUnused()
    {
        var grids = FindVisualChildren<DataGrid>(this);
        foreach (var g in grids)
        {
            if (g.ItemsSource == _vm.UnusedCandidates && g.SelectedItem is UnusedCandidate uc)
                return uc;
        }
        if (_vm.UnusedCandidates.Count > 0) return _vm.UnusedCandidates[0];
        return null;
    }

    private void ApplyMode_Click(object sender, RoutedEventArgs e)
    {
        if (ModeList.SelectedItem is string s)
        {
            var idx = s.IndexOf('(');
            if (idx >= 0)
            {
                var modeStr = s.Substring(idx + 1).TrimEnd(')');
                if (Enum.TryParse<AppMode>(modeStr, out var m))
                    _vm.CurrentMode = m;
            }
        }
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
        System.Windows.MessageBox.Show("Feedback recorded.");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SetCpu.Text, out var cpu)) _config.Config.UnusedCpuThresholdPercent = cpu;
        if (int.TryParse(SetIdle.Text, out var idle)) _config.Config.UnusedIdleMinutes = idle;
        if (int.TryParse(SetRefresh.Text, out var rf)) _config.Config.RefreshIntervalSeconds = rf;
        _config.Config.OnlineLookupEndpoint = SetEndpoint.Text;
        _config.Config.OnlineLookupApiKey = SetApiKey.Text;
        _config.Config.OnlineLookupEnabled = SetOnlineEnabled.IsChecked == true;
        _config.Config.AutoModeSwitch = AutoSwitchCheck.IsChecked == true;
        _config.Config.ProtectedNames = ProtectedListEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Config.Whitelist = WhitelistEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Config.Blacklist = BlacklistEditor.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        _config.Save();
        _vm.UnusedDetector.Config = _config.Config;
        _vm.OnlineLookup.Config = _config.Config;
        System.Windows.MessageBox.Show("Settings saved.");
    }

    private async void BatchL4_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.UnusedCandidates.Count == 0)
        {
            System.Windows.MessageBox.Show("No cleanup candidates.", "Info");
            return;
        }
        var lines = _vm.UnusedCandidates.Select(c => $"- {c.Process.Name} (PID {c.Process.Pid})  Est. reclaim {c.EstimatedReclaimableBytes} bytes  Risk {c.Risk}").ToList();
        long total = _vm.UnusedCandidates.Sum(c => c.EstimatedReclaimableBytes);
        var summary = $"Will force-kill {_vm.UnusedCandidates.Count} processes, est. reclaim {total} bytes:\n\n" + string.Join("\n", lines) +
                      "\n\nImpact: force-kill may cause unsaved data loss and cannot be undone. System/driver/service/security/anticheat processes are excluded.";
        var res = System.Windows.MessageBox.Show(summary, "Batch L4 Confirm (1/2)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;
        var res2 = System.Windows.MessageBox.Show("Second confirmation: execute batch force-kill?", "Batch L4 Confirm (2/2)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res2 != MessageBoxResult.Yes) return;
        _vm.ProgressValue = 0;
        int done = 0; int totalN = _vm.UnusedCandidates.Count;
        foreach (var c in _vm.UnusedCandidates.ToList())
        {
            await _vm.PerformCleanupAsync(c, CleanupActionLevel.L4ForceKill);
            done++;
            _vm.ProgressValue = (double)done / totalN * 100.0;
        }
        _vm.ProgressValue = 100;
        await _vm.RefreshAsync();
        System.Windows.MessageBox.Show("Batch cleanup completed.", "Done");
    }

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
        if (areas.Count == 0) { System.Windows.MessageBox.Show("Please select at least one area.", "Info"); return; }
        if (!_memCleaner.IsAdmin()) { System.Windows.MessageBox.Show("Administrator privileges required.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
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

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        if (_isDarkTheme)
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27));
        }
        else
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF4, 0xF7));
        }
    }

    private void StartAutoModeTimer()
    {
        _autoModeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoModeTimer.Tick += (s, e) =>
        {
            if (AutoSwitchCheck.IsChecked != true) return;
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return;
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                var name = proc.ProcessName.ToLowerInvariant();
                AppMode? target = null;
                if (name.Contains("steam") || name.Contains("epic") || name.Contains("game")) target = AppMode.Game;
                else if (name.Contains("devenv") || name.Contains("code") || name.Contains("rider") || name.Contains("idea")) target = AppMode.Development;
                else if (name.Contains("comfyui") || name.Contains("automatic1111") || name.Contains("invokeai")) target = AppMode.AiGeneration;
                if (target.HasValue && target.Value != _vm.CurrentMode)
                    _vm.CurrentMode = target.Value;
            }
            catch { }
        };
        _autoModeTimer.Start();
    }

    private void StartAutoOptimizeTimer()
    {
        _autoOptimizeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoOptimizeTimer.Tick += (s, e) =>
        {
            if (!_vm.ConfigStore.Config.AutoOptimizeEnabled) return;
            var mem = _vm.ProcessService.GetSystemMemory();
            double freePct = (double)mem.AvailablePhysicalBytes / mem.TotalPhysicalBytes * 100;
            bool shouldClean = false;
            if (_vm.ConfigStore.Config.AutoOptimizeOnLowMemory && freePct < _vm.ConfigStore.Config.AutoOptimizeThresholdPercent)
                shouldClean = true;
            if (_vm.ConfigStore.Config.AutoOptimizeIntervalMinutes > 0 && (DateTime.Now - _lastAutoOptimize).TotalMinutes >= _vm.ConfigStore.Config.AutoOptimizeIntervalMinutes)
                shouldClean = true;
            if (shouldClean)
            {
                _lastAutoOptimize = DateTime.Now;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var areas = new List<MemoryArea> { MemoryArea.StandbyListLowPriority, MemoryArea.WorkingSet };
                    _memCleaner.CleanAreas(areas);
                    await Dispatcher.InvokeAsync(async () => await _vm.RefreshAsync());
                });
            }
        };
        _autoOptimizeTimer.Start();
    }

    private void StartTrayUpdateTimer()
    {
        _trayUpdateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _trayUpdateTimer.Tick += (s, e) =>
        {
            if (_trayIcon == null) return;
            if (_vm.ConfigStore.Config.ShowMemoryInTray)
            {
                var mem = _vm.ProcessService.GetSystemMemory();
                double usedPct = (1.0 - (double)mem.AvailablePhysicalBytes / mem.TotalPhysicalBytes) * 100;
                _trayIcon.Text = $"MemoryTool - {usedPct:F0}% used";
            }
            else
            {
                _trayIcon.Text = "MemoryTool";
            }
        };
        _trayUpdateTimer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static string ResolveDataDir()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoryTool"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryTool"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "data"),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MemoryTool")
        };
        foreach (var c in candidates)
        {
            try
            {
                System.IO.Directory.CreateDirectory(c);
                var test = System.IO.Path.Combine(c, ".writetest");
                System.IO.File.WriteAllText(test, "x");
                System.IO.File.Delete(test);
                return c;
            }
            catch { }
        }
        return candidates[candidates.Length - 1];
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj != null)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                if (child != null && child is T t) yield return t;
                foreach (T childOfChild in FindVisualChildren<T>(child)) yield return childOfChild;
            }
        }
    }
}