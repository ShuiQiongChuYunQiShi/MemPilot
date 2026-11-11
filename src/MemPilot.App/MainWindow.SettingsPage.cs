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
        SetAutoJunkSweep.IsChecked = _config.Config.AutoJunkSweepEnabled;
        SetJunkAdvice.IsChecked = _config.Config.JunkAdviceEnabled;
        SetAutoJunkSweepOrphansOnly.IsChecked = _config.Config.AutoJunkSweepOrphansOnly;
        SetAutoCommitRelief.IsChecked = _config.Config.AutoCommitReliefEnabled;
        SetCommitReliefL3.IsChecked = _config.Config.CommitReliefL3Enabled;
        SetLeaseApi.IsChecked = _config.Config.LeaseApiEnabled;
        SetLeasePort.Text = _config.Config.LeaseApiPort.ToString();
        SetLeaseToken.Text = _vm.LeaseToken;
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
        _vm.Status = "租约令牌已重新生成——记得同步给调用方。";
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
        catch (Exception ex) { _vm.Status = "重建程序清单失败: " + ex.Message; }
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
        _config.Config.AutoJunkSweepOrphansOnly = SetAutoJunkSweepOrphansOnly.IsChecked == true;
        _config.Config.AutoCommitReliefEnabled = SetAutoCommitRelief.IsChecked == true;
        _config.Config.CommitReliefL3Enabled = SetCommitReliefL3.IsChecked == true;
        _config.Config.LeaseApiEnabled = SetLeaseApi.IsChecked == true;
        if (int.TryParse(SetLeasePort.Text, out var leasePort) && leasePort is >= 1024 and <= 65535)
            _config.Config.LeaseApiPort = leasePort;
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
                hotkeyNote = " 全局热键已生效。";
            else
                hotkeyNote = " 全局热键注册失败（格式如 CTRL+SHIFT+M，或被其他程序占用）。";
        }
        _vm.Status = "设置已保存。" + hotkeyNote;
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
        if (areas.Count == 0) { MessageBox.Show("请至少选择一个区域。", "提示"); return; }

        // v0.9.0：WorkingSet 是全机/候选集压缩，给安全子集 + 全屏拒绝
        if (areas.Contains(MemoryArea.WorkingSet))
        {
            if (_vm.Engine.ForegroundFullscreen)
            {
                MessageBox.Show("当前有前台全屏程序（游戏/视频），已拒绝回收工作集——会造成明显卡顿。", "已拒绝", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var audio = new HashSet<int>(_vm.Processes.Where(p => p.AudioActive).Select(p => p.Pid));
            var safe = TrimSelection.BuildSafeTrimSet(_vm.Processes.ToList(), _vm.Engine.LastForegroundPid, audio);
            if (safe.Count == 0)
            {
                MessageBox.Show("没有可安全压缩的进程（工作/游戏/前台进程全部排除）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show($"将只压缩 {safe.Count} 个低负载后台进程（排除 {_vm.Processes.Count - safe.Count} 个工作/游戏/前台/忙碌进程）。继续？",
                    "确认回收工作集", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _memCleaner.SetWorkingSetAllowList(safe.Select(p => p.Pid).ToList());
        }
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

    /// <summary>设置页「立即检查垃圾建议」：忽略晚 19 点时间窗与当日标记，安全过滤照常。</summary>
    private void ForceJunkAdvice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.Engine.ForceJunkAdviceNow(_vm.LastProcessSnapshot());
            _vm.SetActionStatus("已触发垃圾进程检查——有整天未使用的垃圾时，主页会出现建议卡。");
        }
        catch (Exception ex) { _vm.SetActionStatus("触发垃圾检查失败：" + ex.Message); }
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
