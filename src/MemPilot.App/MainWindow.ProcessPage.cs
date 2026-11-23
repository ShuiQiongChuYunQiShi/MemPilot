namespace MemPilot.App;

public partial class MainWindow : Window
{
    // ---- selection preservation ----

    /// <summary>
    /// v0.22.0：右键先把光标下的行设为选中。
    /// 旧实现右键不改变 SelectedItem，菜单动作会落到上一次选中的行；更糟的是
    /// L1–L4 处理器曾回退到「可清理页」的残留选择，导致对错误进程执行挂起/强杀。
    /// </summary>
    private void ProcessGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var d = e.OriginalSource as System.Windows.DependencyObject;
            while (d != null && d is not DataGridRow)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            if (d is DataGridRow row)
            {
                row.IsSelected = true;
                row.Focus();
            }
        }
        catch { /* 非可视元素（如 Run）：保持原选中，右键菜单仍可用 */ }
    }

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
            var id = item.Tag as string ?? "";
            var col = FindColumnById(id);
            item.IsChecked = col != null && col.Visibility == Visibility.Visible;
        }
    }

    private void ColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var id = mi.Tag as string ?? "";
        var col = FindColumnById(id);
        if (col == null) return;

        col.Visibility = mi.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        var hidden = _config.Config.ProcessHiddenColumns ?? new List<string>();
        hidden.RemoveAll(h => string.Equals(h, id, StringComparison.Ordinal));
        if (!mi.IsChecked) hidden.Add(id);
        _config.Config.ProcessHiddenColumns = hidden;
        _vm.ConfigStore.Save();
    }

    /// <summary>
    /// DataGrid 列 Header 资源键 → 稳定 id。v0.10.0：config 只存 id；
    /// v0.19.0：键从「中文显示文本」改为「资源键」——英文界面下显示文本是 "VRAM"，
    /// 旧映射会全部失配，列显隐静默失效。现在用 UiStrings.Get(key) 与当前表头比对。
    /// PID/名称固定可见，不在表里；CPU% 等未本地化的列走 LiteralHeaderToId。
    /// </summary>
    private static readonly Dictionary<string, string> HeaderKeyToId = new(StringComparer.Ordinal)
    {
        ["Col_Program"] = PersistedIds.Column.Program,
        ["Col_Vram"] = PersistedIds.Column.GpuMemory,
        ["Col_MemoryGrowth"] = PersistedIds.Column.MemoryGrowth,
        ["Col_Threads"] = PersistedIds.Column.Threads,
        ["Col_Children"] = PersistedIds.Column.ChildCount,
        ["Col_WindowTitle"] = PersistedIds.Column.WindowTitle,
        ["Col_Vendor"] = PersistedIds.Column.Vendor,
        ["Col_Audio"] = PersistedIds.Column.Audio,
        ["Concept_Protected"] = PersistedIds.Column.IsProtected,
        ["Col_ModeKeep"] = PersistedIds.Column.ModeKeep,
        ["Col_Signature"] = PersistedIds.Column.Signature,
        ["Col_CategoryReason"] = PersistedIds.Column.CategoryReason,
        ["Col_WorkingSet"] = PersistedIds.Column.WorkingSet,
        ["Col_PrivateWs"] = PersistedIds.Column.PrivateWorkingSet,
        ["Home_Commit"] = PersistedIds.Column.Commit,
        ["Col_Category"] = PersistedIds.Column.Category,
        ["Col_Workload"] = PersistedIds.Column.Workload,
    };

    /// <summary>未本地化的表头（英文缩写），直接字面量匹配。</summary>
    private static readonly Dictionary<string, string> LiteralHeaderToId = new(StringComparer.Ordinal)
    {
        ["CPU%"] = PersistedIds.Column.Cpu,
    };

    private static string? IdForHeader(object? header)
    {
        var text = header?.ToString();
        if (string.IsNullOrEmpty(text)) return null;
        if (LiteralHeaderToId.TryGetValue(text, out var direct)) return direct;
        foreach (var (key, id) in HeaderKeyToId)
            if (string.Equals(UiStrings.Get(key), text, StringComparison.Ordinal)) return id;
        return null;
    }

    private DataGridColumn? FindColumnById(string id)
    {
        if (ProcessGrid == null || string.IsNullOrEmpty(id)) return null;
        foreach (var col in ProcessGrid.Columns)
        {
            if (string.Equals(IdForHeader(col.Header), id, StringComparison.Ordinal)) return col;
        }
        return null;
    }

    /// <summary>按配置应用列显隐。PID/名称固定可见（FrozenColumnCount=2），不被配置隐藏。</summary>
    private void ApplyColumnVisibility()
    {
        if (ProcessGrid == null) return;
        var hidden = _config.Config.ProcessHiddenColumns ?? new List<string>();
        foreach (var col in ProcessGrid.Columns)
        {
            // 前两列由 FrozenColumnCount 固定（PID/名称），任何语言下都不参与显隐
            if (col.DisplayIndex < 2) { col.Visibility = Visibility.Visible; continue; }
            var id = IdForHeader(col.Header);
            // 表头没登记到 id 的列（例如后续新加）一律可见——宁可露出也不要凭空藏掉。
            if (id == null) { col.Visibility = Visibility.Visible; continue; }
            col.Visibility = hidden.Any(x => string.Equals(x, id, StringComparison.Ordinal))
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
        if (p == null) { _vm.Status = UiStrings.Get("Process_SelectRow"); return; }
        if (!_vm.Cleanup.Suspended.IsSuspended(p.Pid)) { _vm.Status = UiStrings.F("Process_NotSuspendedByUs", p.Name); return; }
        _vm.ResumeSuspended(new SuspendRecord { Pid = p.Pid, Name = p.Name });
    }

    private void CtxMemPriorityLow_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetMemoryPriority(p.Pid, 1); // VERY_LOW
        _vm.Status = ok ? UiStrings.F("Process_PriorityLowered", p.Name) : UiStrings.Get("Process_PriorityFail");
    }

    private void CtxMemPriorityNormal_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetMemoryPriority(p.Pid, 5); // NORMAL
        _vm.Status = ok ? UiStrings.F("Process_PriorityRestored", p.Name) : UiStrings.Get("Process_PriorityFailShort");
    }

    private void CtxEcoOn_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        bool ok = _vm.ProcessService.SetEcoMode(p.Pid, true);
        _vm.Status = ok ? UiStrings.F("Process_EcoOn", p.Name) : UiStrings.Get("Process_EcoFail");
    }

    private void CtxEcoOff_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) return;
        _vm.ProcessService.SetEcoMode(p.Pid, false);
    }

    // ---- feedback shortcuts from the process grid ----

    /// <summary>v0.22.0：空状态里的「清除筛选」——把 UI 与 VM 三个筛选一起复位。</summary>
    private void ClearProcessFilters_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox != null) SearchBox.Text = "";
        if (WorkloadFilterBox != null && WorkloadFilterBox.SelectedIndex != 0) WorkloadFilterBox.SelectedIndex = 0;
        if (ChipAll != null && ChipAll.IsChecked != true)
        {
            foreach (var name in new[] { "ChipSystem", "ChipGame", "ChipAi", "ChipUnknown" })
                if (FindName(name) is System.Windows.Controls.Primitives.ToggleButton other) other.IsChecked = false;
            ChipAll.IsChecked = true;
        }
        _vm.ClearProcessFilters();
    }

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
        if (p == null) { _vm.Status = UiStrings.Get("Process_SelectRow"); return; }
        var win = new ThreadWindow(p, _vm.ProcessService) { Owner = this };
        win.Show();
    }

    /// <summary>右键「标记用途」（v0.4.0）：人工纠正优先于规则，下一轮刷新即生效。</summary>
    private void CtxWorkload_Click(object sender, RoutedEventArgs e)
    {
        var p = GetSelectedProcess();
        if (p == null) { _vm.Status = UiStrings.Get("Process_SelectRow"); return; }
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
            UserNote = keep ? UiStrings.Get("Process_MarkKeep") : clean ? UiStrings.Get("Process_MarkCleanable") : UiStrings.Get("Process_MarkSystem"),
            MarkKeep = keep,
            MarkCleanable = clean,
            MarkSystem = system,
            Scope = "process"
        };
        _vm.AddFeedback(entry);
        var markLabel = keep ? UiStrings.Get("Concept_KeepMark")
            : clean ? UiStrings.Get("Concept_CleanableMark") : UiStrings.Get("Term_SystemProcess");
        _vm.Status = UiStrings.F("Process_MarkRecorded", p.Name, markLabel);
    }
}
