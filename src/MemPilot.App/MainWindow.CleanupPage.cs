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
}
