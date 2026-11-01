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
        // 空表单会写进一条永远匹配不到任何进程的记录（v0.3.6 真机见过一条），
        // 所以这里必须校验：至少要有进程名或路径。
        var name = (FbName.Text ?? "").Trim();
        var path = (FbPath.Text ?? "").Trim().Trim('"');
        if (name.Length == 0 && path.Length == 0)
        {
            _vm.Status = "请至少填写进程名或路径——空反馈不会匹配任何进程，等于没提交。";
            FbName.Focus();
            return;
        }

        var entry = new FeedbackEntry
        {
            ProcessName = name,
            ExecutablePath = path,
            ReportedCategory = FbCategory.SelectedItem is ProcessCategory c ? c : ProcessCategory.Unknown,
            UserNote = (FbNote.Text ?? "").Trim(),
            MarkKeep = FbKeep.IsChecked == true,
            MarkCleanable = FbClean.IsChecked == true,
            MarkSystem = FbSystem.IsChecked == true,
            Scope = "process"
        };
        _vm.AddFeedback(entry);
        // 不用模态弹窗（用户偏好）：状态栏 + 清空表单，列表里立刻能看到这一行
        FbName.Text = ""; FbPath.Text = ""; FbNote.Text = "";
        FbKeep.IsChecked = false; FbClean.IsChecked = false; FbSystem.IsChecked = false;
        _vm.Status = $"已记录反馈：{entry.ProcessName} → {entry.EffectText}（下次刷新生效，右键该行可撤销）。";
        _ = _vm.RefreshAsync();
    }

    /// <summary>撤销一条反馈（v0.3.7）：标记错了要能撤回，否则只能去改 feedback.json。</summary>
    private void DeleteFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (FeedbackGrid?.SelectedItem is not FeedbackEntry fe)
        {
            _vm.Status = "请先在列表里选中一条反馈再撤销。";
            return;
        }
        if (_vm.RemoveFeedback(fe))
        {
            _vm.Status = $"已撤销 {fe.ProcessName} 的反馈，下次刷新生效。";
            _ = _vm.RefreshAsync();
        }
        else _vm.Status = "撤销失败：记录不存在（可能已被删除）。";
    }
}
