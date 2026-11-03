namespace MemPilot.App;

public partial class MainWindow : Window
{
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _vm.SearchText = SearchBox.Text;

    // ---- 进程页：用途筛选 + 列设置（棱镜 ui/table-density、product/classification-quality）----

    private void WorkloadFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || WorkloadFilterBox == null) return;
        var tag = (WorkloadFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _vm.WorkloadFilter = tag;
    }

    private void ColumnMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu == null) return;
        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        fe.ContextMenu.IsOpen = true;
    }

    private void ColumnMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        foreach (var item in cm.Items.OfType<MenuItem>())
        {
            var header = item.Tag as string ?? "";
            var col = FindColumn(header);
            item.IsChecked = col != null && col.Visibility == Visibility.Visible;
        }
    }

    private void ColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var header = mi.Tag as string ?? "";
        var col = FindColumn(header);
        if (col == null) return;

        col.Visibility = mi.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        var hidden = _config.Config.ProcessHiddenColumns ?? new List<string>();
        hidden.RemoveAll(h => string.Equals(h, header, StringComparison.Ordinal));
        if (!mi.IsChecked) hidden.Add(header);
        _config.Config.ProcessHiddenColumns = hidden;
        _vm.ConfigStore.Save();
    }

    private DataGridColumn? FindColumn(string header)
        => ProcessGrid?.Columns.FirstOrDefault(c => c.Header is string h && string.Equals(h, header, StringComparison.Ordinal));

    /// <summary>按配置应用列显隐。PID/名称固定可见（FrozenColumnCount=2），不被配置隐藏。</summary>
    private void ApplyColumnVisibility()
    {
        if (ProcessGrid == null) return;
        var hidden = _config.Config.ProcessHiddenColumns ?? new List<string>();
        foreach (var col in ProcessGrid.Columns)
        {
            if (col.Header is not string h) continue;
            if (h is "PID" or "名称") { col.Visibility = Visibility.Visible; continue; }
            col.Visibility = hidden.Any(x => string.Equals(x, h, StringComparison.Ordinal))
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    /// <summary>把配置里的用途筛选同步到下拉框（初始化时调用，避免触发一次多余的 Save）。</summary>
    private void ApplyWorkloadFilterToUi()
    {
        if (WorkloadFilterBox == null) return;
        var want = _config.Config.WorkloadFilter ?? "";
        WorkloadFilterBox.SelectedIndex = 0;
        foreach (var item in WorkloadFilterBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, want, StringComparison.Ordinal))
            {
                WorkloadFilterBox.SelectedItem = item;
                break;
            }
        }
    }

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

    /// <summary>v0.6.0：分类筛选芯片（互斥；全部=清空）。</summary>
    private void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;
        // 互斥：选中自己、清掉兄弟；点已选中的"全部"无副作用
        foreach (var name in new[] { "ChipAll", "ChipSystem", "ChipGame", "ChipAi", "ChipUnknown" })
        {
            if (FindName(name) is System.Windows.Controls.Primitives.ToggleButton other && !ReferenceEquals(other, tb) && other.IsChecked == true)
                other.IsChecked = false;
        }
        if (!tb.IsChecked.HasValue || tb.IsChecked == false) tb.IsChecked = true; // 点已选中的芯片保持选中

        _vm.CategoryChip = tb.Name switch
        {
            "ChipSystem" => "system",
            "ChipGame" => "game",
            "ChipAi" => "ai",
            "ChipUnknown" => "unknown",
            _ => null
        };
    }

    /// <summary>v0.6.0：线程管理从独立页降级为右键入口（TID 级挂起/恢复）。</summary>
    private void ManageThreads_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) { _vm.Status = "请先在进程列表选择一行。"; return; }
        var win = new ThreadWindow(p, _vm.ProcessService) { Owner = this };
        win.Show();
    }

    /// <summary>右键「标记用途」（v0.4.0）：人工纠正优先于规则，下一轮刷新即生效。</summary>
    private void CtxWorkload_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) { _vm.Status = "请先在进程列表选择一行。"; return; }
        var tag = (sender as MenuItem)?.Tag as string ?? "";
        WorkloadKind? kind = Enum.TryParse<WorkloadKind>(tag, out var k) ? k : null;
        _vm.MarkWorkload(p, kind);
    }

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
}
