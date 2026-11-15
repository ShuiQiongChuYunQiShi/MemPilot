namespace MemPilot.App;

public partial class MainWindow : Window
{
    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (_compact != null) { _compact.Activate(); return; }
        _compact = new CompactWindow(_vm, _config, OnCompactMakeRoom, ShowWindow);
        _compact.Closed += (s, e) => _compact = null;
        _compact.Show();
        Hide(); // the mini bar replaces the main window; tray stays alive
        UpdateUiActivity();
    }

    private void LoadHistory_Click(object sender, RoutedEventArgs e) => _vm.LoadHistory();

    // ---- top bar ----

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private void ToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        UpdateUiActivity();
    }

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
        _vm.Status = _config.Config.AutoModeSwitch ? UiStrings.Get("Tabs_AutoSwitchOn") : UiStrings.Get("Tabs_AutoSwitchOff");
    }

    // ---- home navigation ----

    /// <summary>v0.6.0：路由键从 emoji 前缀改为稳定 Tag（改 Header 文字/图标不再破坏路由）。</summary>
    private void SwitchToTab(string key)
    {
        foreach (var item in RootTabs.Items.OfType<TabItem>())
        {
            if (Equals(item.Tag, key)) { RootTabs.SelectedItem = item; return; }
        }
    }

    private void GoClean_Click(object sender, RoutedEventArgs e) => SwitchToTab("cleanup");
    private void GoDecisions_Click(object sender, RoutedEventArgs e) => SwitchToTab("decisions");
    private void GoSysMem_Click(object sender, RoutedEventArgs e) => SwitchToTab("sysmem");

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return;
        if (_vm == null) return;   // v0.13.1：IsSelected=True 在 BAML 加载期就触发本事件，_vm 尚未构造
        var tag = e.AddedItems.OfType<TabItem>().FirstOrDefault()?.Tag?.ToString() ?? "";
        // v0.13.1 兜底：分组头（裸 TextBlock）占据 items[0]，TabControl 初始默认选中可能落在
        // 它上面（v0.6.0 起的"打开界面空白"根因）。若选中项不是任何已知页签，强制回主页，
        // 使"分组头即 item"的结构永远不可能再产生空白窗。
        if (tag == "")
        {
            SwitchToTab("home");
            return;
        }
        // 页签感知：VM 只重建当前可见页签的集合（性能优化，见 UpdateCollections 注释）。
        // 这里把稳定 Tag 翻译成 TabRebuildPolicy 的键（Core 不感知 UI 文案）。
        var key = tag switch
        {
            "home" => "🏠", "process" => "📋", "cleanup" => "🧹",
            "suspend" => "⏸", "decisions" => "🧠", _ => "other"
        };
        _vm.OnTabActivated(key);
        if (tag == "mode") InitModeTab();
        else if (tag == "settings") InitSettingsTab();
        else if (tag == "history") _vm.LoadHistory();
    }
    private void InitModeTab()
    {
        if (_modeTabInit || ModeList == null) return;
        _modeTabInit = true;
        foreach (var kv in _config.Config.ModeProfiles)
            ModeList.Items.Add(new ListBoxItem { Content = $"{kv.Value.Name} ({kv.Key})", Tag = kv.Key });
    }

    // ---- suspended tab ----

    private void ResumeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SuspendedList.SelectedItem is SuspendRecord rec)
            _vm.ResumeSuspended(rec);
        else _vm.Status = UiStrings.Get("Tabs_SelectSuspend");
    }

    private void ResumeAll_Click(object sender, RoutedEventArgs e) => _vm.ResumeAllSuspended();

    private void RestartDisposal_Click(object sender, RoutedEventArgs e)
    {
        if (DisposalsGrid == null || DisposalsGrid.SelectedItem is not DisposalRecord rec) { _vm.Status = UiStrings.Get("Tabs_SelectDisposal"); return; }
        if (MessageBox.Show(UiStrings.F("Tabs_RelaunchConfirm", rec.Name, rec.ExePath), UiStrings.Get("Common_Confirm"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (_vm.RestartDisposal(rec))
            _vm.Status = UiStrings.F("Tabs_Relaunched", rec.Name);
        else
        {
            _vm.Status = UiStrings.Get("Tabs_LaunchFailed");
            _tray?.ShowBalloon(UiStrings.Get("Tabs_RelaunchFailed"), UiStrings.Get("Tabs_ExeMissing"), warning: true);
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
            ModeEditorHint.Text = UiStrings.F("Tabs_EditingMode", profile.Name);
        }
    }

    private void SaveModeRules_Click(object sender, RoutedEventArgs e)
    {
        if (ModeList.SelectedItem is not ListBoxItem { Tag: AppMode m } ||
            !_vm.ConfigStore.Config.ModeProfiles.TryGetValue(m, out var profile))
        {
            _vm.Status = UiStrings.Get("Tabs_SelectMode");
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
        MessageBox.Show(UiStrings.F("Tabs_ModeRulesSaved", profile.Name), UiStrings.Get("Common_Done"));
    }

    /// <summary>撤销一条反馈（v0.3.7）：标记错了要能撤回，否则只能去改 feedback.json。</summary>
    private void DeleteFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (FeedbackGrid?.SelectedItem is not FeedbackEntry fe)
        {
            _vm.Status = UiStrings.Get("Tabs_SelectFeedback");
            return;
        }
        if (_vm.RemoveFeedback(fe))
        {
            _vm.Status = UiStrings.F("Tabs_FeedbackUndone", fe.ProcessName);
            _ = _vm.RefreshAsync();
        }
        else _vm.Status = UiStrings.Get("Tabs_UndoFailed");
    }
}
