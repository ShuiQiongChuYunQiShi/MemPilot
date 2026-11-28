namespace MemPilot.App;

public partial class MainWindow : Window
{
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
        _vm.Status = want ? UiStrings.Get("Settings_AutoStartOn") : UiStrings.Get("Settings_AutoStartOff");
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
    private bool _languageInit;

    /// <summary>v0.19.0：语言切换。DynamicResource 自动刷新 XAML；code-behind 文案走
    /// WpfStrings.Changed 事件重建（状态栏在下一次刷新时更新）。</summary>
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_languageInit || _config == null || SetLanguage?.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "auto";
        if (tag == _config.Config.Language) return;
        _config.Config.Language = tag;
        _config.Save();
        try
        {
            Localization.WpfStrings.Apply(tag);
            _vm.Status = "";   // 让下一次刷新重建状态栏文案
            _ = _vm.RefreshAsync();
        }
        catch (Exception ex) { MemPilot.Helpers.Diag.Record("i18n:switch", ex); }
    }

    private void InitSettingsTab()
    {
        if (_settingsTabInit || SetCpu == null) return;
        _settingsTabInit = true;
        _languageInit = true;
        var lang = _config.Config.Language ?? "auto";
        SetLanguage.SelectedIndex = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 2
            : lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _languageInit = false;
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
        SetAutoJunkSweep.IsChecked = _config.Config.AutoJunkSweepEnabled;
        SetJunkAdvice.IsChecked = _config.Config.JunkAdviceEnabled;
        SetLongResidentAdvice.IsChecked = _config.Config.LongResidentAdviceEnabled;
        SetWslHint.IsChecked = _config.Config.WslShutdownHintEnabled;
        SetAutoJunkSweepOrphansOnly.IsChecked = _config.Config.AutoJunkSweepOrphansOnly;
        SetAutoCommitRelief.IsChecked = _config.Config.AutoCommitReliefEnabled;
        SetCommitReliefL3.IsChecked = _config.Config.CommitReliefL3Enabled;
        SetLeaseApi.IsChecked = _config.Config.LeaseApiEnabled;
        SetLeasePort.Text = _config.Config.LeaseApiPort.ToString();
        SetLeaseToken.Text = _vm.LeaseToken;
        SetAutoLeaseAi.IsChecked = _config.Config.AutoLeaseForAiEnabled;
        SetAutoLeaseAiGb.Text = _config.Config.AutoLeaseForAiGb.ToString("0.#");
        SetShowAlerts.IsChecked = _config.Config.ShowAlerts;
        SetGpuSampling.IsChecked = _config.Config.GpuSamplingEnabled;
        SelectGpuInterval(_config.Config.GpuSampleIntervalSeconds);
        SetAutoStart.IsChecked = StartupService.IsAutoStart();
        SetStartMinimized.IsChecked = _config.Config.StartMinimizedToTray;
        SetHotkeyEnabled.IsChecked = _config.Config.GlobalHotkeyEnabled;
        SetHotkey.Text = _config.Config.GlobalHotkey;
        SetEventLog.IsChecked = _config.Config.WriteEventLogAudit;
        SetResumeOnModeChange.IsChecked = _config.Config.ResumeSuspendedOnModeChange;
        ProtectedListEditor.Text = string.Join("\n", _config.Config.ProtectedNames);
        WhitelistEditor.Text = string.Join("\n", _config.Config.Whitelist);
        BlacklistEditor.Text = string.Join("\n", _config.Config.Blacklist);
        _vm.RefreshShadowCalibration(); // 影子结算：进设置页即算一次（1000 条内存内，无 IO）
        _vm.RefreshDiagnostics();       // 落盘诊断：失败计数 + 最近一条
    }

    private void RefreshShadowCalibration_Click(object sender, RoutedEventArgs e) => _vm.RefreshShadowCalibration();

    private void RegenerateLeaseToken_Click(object sender, RoutedEventArgs e)
    {
        _vm.RegenerateLeaseToken();
        SetLeaseToken.Text = _vm.LeaseToken;
        _vm.Status = UiStrings.Get("Settings_TokenRegenerated");
    }

    // ---- 显存采样设置（v0.3.4）----

    private void SelectGpuInterval(int seconds)
    {
        foreach (var item in SetGpuInterval.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string t && int.TryParse(t, out var s) && s == seconds)
            {
                SetGpuInterval.SelectedItem = item;
                return;
            }
        }
        SetGpuInterval.SelectedIndex = 1; // 30s
    }

    private int SelectedGpuInterval()
        => SetGpuInterval.SelectedItem is ComboBoxItem { Tag: string t } && int.TryParse(t, out var s)
            ? Math.Clamp(s, 15, 300)
            : 30;

    // ---- 程序清单（v0.3.0）----

    private async void RebuildCatalog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.RebuildProgramCatalogAsync();
            await _vm.RefreshAsync();
        }
        catch (Exception ex) { _vm.Status = UiStrings.Get("Settings_CatalogFailed") + ex.Message; }
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
        _config.Config.AutoJunkSweepEnabled = SetAutoJunkSweep.IsChecked == true;
        _config.Config.JunkAdviceEnabled = SetJunkAdvice.IsChecked == true;
        _config.Config.LongResidentAdviceEnabled = SetLongResidentAdvice.IsChecked == true;
        _config.Config.WslShutdownHintEnabled = SetWslHint.IsChecked == true;
        _config.Config.AutoJunkSweepOrphansOnly = SetAutoJunkSweepOrphansOnly.IsChecked == true;
        _config.Config.AutoCommitReliefEnabled = SetAutoCommitRelief.IsChecked == true;
        _config.Config.CommitReliefL3Enabled = SetCommitReliefL3.IsChecked == true;
        _config.Config.LeaseApiEnabled = SetLeaseApi.IsChecked == true;
        if (int.TryParse(SetLeasePort.Text, out var leasePort) && leasePort is >= 1024 and <= 65535)
            _config.Config.LeaseApiPort = leasePort;
        _config.Config.AutoLeaseForAiEnabled = SetAutoLeaseAi.IsChecked == true;
        if (double.TryParse(SetAutoLeaseAiGb.Text, out var autoGb))
            _config.Config.AutoLeaseForAiGb = Math.Clamp(autoGb, 1, 64);
        _config.Config.ShowAlerts = SetShowAlerts.IsChecked == true;
        _config.Config.GpuSamplingEnabled = SetGpuSampling.IsChecked == true;
        _config.Config.GpuSampleIntervalSeconds = SelectedGpuInterval();
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
                hotkeyNote = UiStrings.Get("Settings_HotkeyOk");
            else
                hotkeyNote = UiStrings.Get("Settings_HotkeyFail");
        }
        _vm.Status = UiStrings.Get("Settings_Saved") + hotkeyNote;
        // v0.11.0：删除"设置已保存"气球——routine 动作发托盘通知是噪音；热键注册失败等异常已在状态栏/日志可见
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
        if (ChkCombinedMemory?.IsChecked == true) areas.Add(MemoryArea.CombinedMemory);
        if (ChkWorkingSet.IsChecked == true) areas.Add(MemoryArea.WorkingSet);
        if (areas.Count == 0) { MessageBox.Show(UiStrings.Get("Settings_SelectArea"), UiStrings.Get("Common_Notice")); return; }

        // v0.9.0：WorkingSet 是全机/候选集压缩，给安全子集 + 全屏拒绝
        if (areas.Contains(MemoryArea.WorkingSet))
        {
            if (_vm.Engine.ForegroundFullscreen)
            {
                MessageBox.Show(UiStrings.Get("Settings_FullscreenRefused"), UiStrings.Get("Common_Rejected"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var audio = new HashSet<int>(_vm.Processes.Where(p => p.AudioActive).Select(p => p.Pid));
            var safe = TrimSelection.BuildSafeTrimSet(_vm.Processes.ToList(), _vm.Engine.LastForegroundPid, audio,
                releaseRatio: _vm.Cleanup.TrimFeedback.RatioOf);
            if (safe.Count == 0)
            {
                MessageBox.Show(UiStrings.Get("Settings_NoSafeProcesses"), UiStrings.Get("Common_Notice"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(UiStrings.F("Settings_TrimConfirm", safe.Count, _vm.Processes.Count - safe.Count),
                    UiStrings.Get("Btn_ConfirmTrim"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _memCleaner.SetWorkingSetAllowList(safe.Select(p => p.Pid).ToList());
        }
        if (areas.Contains(MemoryArea.StandbyList) &&
            MessageBox.Show(UiStrings.Get("Settings_StandbyWarning"), UiStrings.Get("Common_Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (!Elevation.IsAdmin())
        {
            var argList = areas.Select(a => "/" + a).ToList();
            var res2 = MessageBox.Show(
                UiStrings.Get("Settings_ElevationPrompt"),
                UiStrings.Get("Settings_NeedElevation"), MessageBoxButton.YesNo, MessageBoxImage.Question);
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

    /// <summary>设置页「立即检查垃圾建议」：忽略晚 19 点时间窗与当日标记，安全过滤照常。</summary>
    private void ForceJunkAdvice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.Engine.ForceJunkAdviceNow(_vm.LastProcessSnapshot());
            _vm.SetActionStatus(UiStrings.Get("Settings_JunkCheckTriggered"));
        }
        catch (Exception ex) { _vm.SetActionStatus(UiStrings.Get("Settings_JunkCheckFailed") + ex.Message); }
    }

    private void SelectAllAreas_Click(object sender, RoutedEventArgs e)
    {
        // v0.11.0 P0-4：漏了 ChkCombinedMemory——"全选"只勾 8/9，用户以为全选了其实没有
        ChkCombinedPageList.IsChecked = true; ChkModifiedFileCache.IsChecked = true; ChkModifiedPageList.IsChecked = true;
        ChkRegistryCache.IsChecked = true; ChkStandbyList.IsChecked = true; ChkStandbyListLow.IsChecked = true;
        ChkSystemFileCache.IsChecked = true; ChkWorkingSet.IsChecked = true; ChkCombinedMemory.IsChecked = true;
    }

    private void DeselectAllAreas_Click(object sender, RoutedEventArgs e)
    {
        ChkCombinedPageList.IsChecked = false; ChkModifiedFileCache.IsChecked = false; ChkModifiedPageList.IsChecked = false;
        ChkRegistryCache.IsChecked = false; ChkStandbyList.IsChecked = false; ChkStandbyListLow.IsChecked = false;
        ChkSystemFileCache.IsChecked = false; ChkWorkingSet.IsChecked = false; ChkCombinedMemory.IsChecked = false;
    }
}
