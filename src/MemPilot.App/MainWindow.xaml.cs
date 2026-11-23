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
        RegisterKeyBindings();   // v0.11.0：Esc/F5/Ctrl+F 全局键
        var dataDir = App.ResolveDataDir();
        _config = new ConfigStore(dataDir);
        var audit = new AuditLogger(dataDir, _config.Config.ArchiveRetentionMonths);
        var feedback = new FeedbackStore(dataDir);
        _vm = new MainViewModel(_config, audit, feedback);
        DataContext = _vm;
        // 告警触达：引擎的 Alert 事件 → 托盘气球（主页横幅由 VM 内部处理）。
        // v0.11.0（UX 摸排 P1-3 通知分级）：按标题前缀做 30 分钟冷却——
        // 提交告急在阈值附近抖动时曾会反复弹（实测 04:39/07:18/10:01 每小时一条）；
        // 冷却后同类告警只写决策日志，不再打扰。
        _vm.AlertRaised += (title, message, warning) =>
            Dispatcher.InvokeAsync(() =>
            {
                var key = title.Split('（')[0].Split('-')[0].Trim();
                if (_lastBalloonUtc.TryGetValue(key, out var last) &&
                    (DateTime.UtcNow - last).TotalMinutes < 30) return;
                _lastBalloonUtc[key] = DateTime.UtcNow;
                _tray?.ShowBalloon(title, message, warning);
            });

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
            _tray?.ShowBalloon(UiStrings.Get("Tray_BlockedTitle"),
                UiStrings.Get("Tray_BlockedBody"), warning: true);
            _vm.Status = UiStrings.Get("Tray_BlockedStatus");
        });
        _tray.DoubleClick += () => Dispatcher.InvokeAsync(ShowWindow);
        _tray.RightClick += () => Dispatcher.InvokeAsync(ShowTrayMenu);
        _tray.SetTooltip("MemPilot");
        _trayMenu = new System.Windows.Controls.ContextMenu
        {
            Items =
            {
                new System.Windows.Controls.MenuItem { Header = UiStrings.Get("Tray_Show") },
                new System.Windows.Controls.MenuItem { Header = UiStrings.Get("Common_Refresh") },
                new System.Windows.Controls.MenuItem { Header = UiStrings.Get("Tray_Exit") }
            }
        };
        var miShow = (System.Windows.Controls.MenuItem)_trayMenu.Items[0];
        var miRefresh = (System.Windows.Controls.MenuItem)_trayMenu.Items[1];
        var miExit = (System.Windows.Controls.MenuItem)_trayMenu.Items[2];
        miShow.Click += (s, e) => ShowWindow();
        miRefresh.Click += async (s, e) => await _vm.RefreshAsync();
        miExit.Click += (s, e) => { _allowClose = true; _vm.Shutdown(); _tray?.Dispose(); Application.Current.Shutdown(); };

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
            Dispatcher.InvokeAsync(() => _tray?.ShowBalloon(UiStrings.Get("Tray_ModeSwitched"),
                UiStrings.F("Tray_ModeSwitchedBody", e.Mode, e.Reason)));

        App.ShowRequested += () => Dispatcher.InvokeAsync(ShowWindow);
        Closing += MainWindow_Closing;
        _vm.CollectionsRebuilt += (s, e) => { RestoreSelection(); ReapplySort(); };
        // v0.22.0：搜索/筛选走 ApplyFilter 直接替换集合（不发 CollectionsRebuilt），排序也要跟着重放
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FilteredProcesses))
                Dispatcher.BeginInvoke(new Action(ReapplySort));
        };
        Loaded += async (s, e) =>
        {
            SyncModeRadios(_vm.CurrentMode);
            if (AutoSwitchCheck != null) AutoSwitchCheck.IsChecked = _config.Config.AutoModeSwitch;
            ApplyColumnVisibility();      // 列设置（默认收起 9 个低频列）
            CaptureDefaultColumnWidths(); // v0.22.0：先记下 XAML 默认列宽（「恢复默认列」要用）
            ApplyColumnWidths();          // v0.22.0：再应用用户上次的列宽
            ApplyWorkloadFilterToUi();    // 用途筛选（上次选择）
            ApplyFirstRunNotice();        // v0.10.0：首次运行说明横幅
            UpdateUiActivity(); // start-minimized sessions begin in lightweight mode
            await _vm.RefreshAsync();
            _vm.StartEngine();          // resident engine: periodic refresh + policies
            StartTrayUpdateTimer();
        };
        StateChanged += (s, e) => UpdateUiActivity();
        IsVisibleChanged += (s, e) => UpdateUiActivity();
    }

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
        _ = _vm.RefreshAsync(); // 打开即刷新：GPU/趋势等隐藏期间未更新的字段立刻补齐
        // v0.3.6：从托盘恢复时页签没有变化，SelectionChanged 不会再触发 → 停在历史页会一直空着。
        // 这里按当前可见页签主动补一次统计（LoadHistory 内部只做 0.8s 防连击，代价约 10ms）。
        if (RootTabs?.SelectedItem is TabItem { Tag: "history" })
            _vm.LoadHistory();
    }

    /// <summary>Lightweight mode: while the window is hidden/minimized the refresh loop runs
    /// at a slower cadence and skips rebuilding UI collections — the engine keeps deciding,
    /// only the tray numbers stay fresh. A memory manager must not be the memory hog.</summary>
    private DateTime _lastSelfTrimUtc = DateTime.MinValue;
    /// <summary>托盘气球按标题前缀的 30 分钟冷却（v0.11.0 通知分级）。</summary>
    private readonly Dictionary<string, DateTime> _lastBalloonUtc = new();
    private int _promoteAttempts;

    /// <summary>Windows 11 defaults new tray icons into the hidden overflow ("^"). Promote
    /// ourselves in the shell's NotifyIconSettings so the icon is always visible in the
    /// main tray area — the programmatic equivalent of dragging it out once.
    ///
    /// v0.10.0：只在用户真的要托盘显示（ShowMemoryInTray）时才动注册表。
    /// 这仍然是对别人操作系统的副作用，所以：不勾就不改、失败一律静默、最多试 100 次。</summary>
    private void PromoteTrayIcon()
    {
        if (_config?.Config?.ShowMemoryInTray != true) return;
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
                    _vm.Status = UiStrings.Get("Tray_Pinned");
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
        // v0.18.0：频率由引擎统一裁决（压力提频 / UI 可见按配置 / 托盘无压力降频），
        // 壳层只上报"用户是否在看"。旧的 SetIntervalSeconds(base*3) 会与压力提频互相覆盖。
        _vm.Engine.UiActive = active;
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
        _vm.Shutdown();
        SaveColumnWidths();   // v0.22.0：退出前保存列宽（关闭到托盘时不保存）
        _tray?.Dispose();
        _hotkey?.Dispose();
    }

    private bool _syncingModeRadios;

    private bool _modeTabInit;

    private bool _settingsTabInit;

    // ---- theme ----

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        var res = Application.Current.Resources;
        void Set(string key, byte r, byte g, byte b) => res[key] = new SolidColorBrush(Color.FromRgb(r, g, b));
        void SetA(string key, byte a, byte r, byte g, byte b) => res[key] = new SolidColorBrush(Color.FromArgb(a, r, g, b));
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
            // v0.13.0：v0.12 新增的语义 token 纳入换色表（此前 21 个 token 不换，暗色会花）
            Set("NavRailBg", 0x0D, 0x15, 0x22); Set("NavHoverBg", 0x1E, 0x29, 0x3B);
            Set("NavActiveBg", 0x24, 0x2F, 0x44); Set("NavActiveFg", 0x8B, 0x9C, 0xFE);
            Set("NavHoverFg", 0xCB, 0xD5, 0xE1); Set("NavSelectedBg", 0x0D, 0x15, 0x22);
            Set("HeaderBg", 0x0D, 0x15, 0x22);
            Set("GridHeaderBg", 0x17, 0x22, 0x33); Set("GridHeaderBorder", 0x33, 0x41, 0x55);
            Set("GridLine", 0x26, 0x33, 0x4A);
            Set("NoticeBg", 0x2B, 0x24, 0x15); Set("NoticeBorder", 0x7A, 0x5A, 0x1E);
            SetA("TrendAreaFill", 0x33, 0x4F, 0x46, 0xE5); SetA("HistoryAreaFill", 0x33, 0x25, 0x63, 0xEB);
            SetA("GpuAreaFill", 0x33, 0x55, 0xC8, 0xA5);
            Set("TimestampMuted", 0x64, 0x74, 0x8B); Set("TextMuted", 0x7B, 0x88, 0xA1);
            Set("WarnBg", 0x2B, 0x24, 0x15); Set("WarnFg", 0xFB, 0xBF, 0x24); Set("WarnInk", 0xF3, 0xE4, 0xB9);
            SetA("WindowShadow", 0xFF, 0x00, 0x00, 0x00);
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
            // 浅色分支：恢复 App.xaml 的默认值（保证亮→暗→亮往返不残留）
            Set("NavRailBg", 0xFF, 0xFF, 0xFF); Set("NavHoverBg", 0xF5, 0xF5, 0xF9);
            Set("NavActiveBg", 0xEE, 0xF2, 0xFF); Set("NavActiveFg", 0x4F, 0x46, 0xE5);
            Set("NavHoverFg", 0x33, 0x41, 0x55); Set("NavSelectedBg", 0x1E, 0x29, 0x3B);
            Set("HeaderBg", 0x0F, 0x17, 0x2A);
            Set("GridHeaderBg", 0xFA, 0xFA, 0xFC); Set("GridHeaderBorder", 0x37, 0x41, 0x51);
            Set("GridLine", 0xE5, 0xE7, 0xEB);
            Set("NoticeBg", 0xFF, 0xF8, 0xE6); Set("NoticeBorder", 0xF0, 0xC3, 0x6D);
            SetA("TrendAreaFill", 0x33, 0x4F, 0x46, 0xE5); SetA("HistoryAreaFill", 0x33, 0x25, 0x63, 0xEB);
            SetA("GpuAreaFill", 0x33, 0x55, 0xC8, 0xA5);
            Set("TimestampMuted", 0x94, 0xA3, 0xB8); Set("TextMuted", 0x8A, 0x8A, 0xA3);
            Set("WarnBg", 0xFF, 0xF9, 0xEC); Set("WarnFg", 0x7A, 0x4B, 0x05); Set("WarnInk", 0x6B, 0x42, 0x07);
            SetA("WindowShadow", 0xFF, 0x17, 0x17, 0x2B);
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
                _tray.SetTooltip(UiStrings.F("Tray_Tooltip", _vm.SysMem.MemoryLoadPercent, _vm.AvailableMemoryText, _vm.SuspendedProcesses.Count));
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
