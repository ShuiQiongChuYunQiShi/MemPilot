using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

/// <summary>可清理页：候选重建、单条/批量处置、垃圾清扫、腾挪兜底与挂起恢复。</summary>
public partial class MainViewModel
{
    private void RebuildUnused()
    {
        UnusedCandidates.Clear();
        // 垃圾常驻置顶（Bloatware 在候选里优先展示），其余按可回收量降序；并附上驻留分
        foreach (var u in _lastUnused.OrderByDescending(x => x.Process.Category == ProcessCategory.Bloatware)
                                     .ThenByDescending(x => x.EstimatedReclaimableBytes))
        {
            u.JunkScore = Behaviors.JunkScore(u.Process.Name);
            UnusedCandidates.Add(u);
        }
        RebuildReviewQueue();
    }

    public async Task PerformCleanupAsync(UnusedCandidate cand, CleanupActionLevel level)
    {
        // graceful close may wait seconds for exit — never block the UI thread
        await Task.Run(() => Cleanup.Execute(cand.Process, level, ConfigStore.Config.GracefulCloseTimeoutSeconds, escalateToKill: false)).ConfigureAwait(true);
        ReloadAudit();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            DisposalRecords.Clear();
            foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        });
        await Task.Delay(600).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
    }

    /// <summary>
    /// 一键压缩低负载后台的工作集（只压缩、不结束任何进程）。
    /// v0.9.2：改为只压安全子集——原来传全部进程，会把游戏/AI/工作进程一起压（与 README 承诺矛盾）。
    /// </summary>
    private DateTime _lastBatchTrimUtc = DateTime.MinValue;

    public async Task<string> TrimAllWorkingSetsAsync()
    {
        // v0.14：双发去重——实测连续两次调用第二次仅 +19~419 MB（第一次已把可回收的都收了）
        if ((DateTime.UtcNow - _lastBatchTrimUtc).TotalMinutes < 10)
        {
            var dup = UiStrings.Get("Cleanup_BatchCooldown");
            Status = dup;
            return dup;
        }
        var all = _lastProcs.ToList();
        var audio = new HashSet<int>(all.Where(p => p.AudioActive).Select(p => p.Pid));
        var procs = TrimSelection.BuildSafeTrimSet(all, Engine.LastForegroundPid, audio);
        if (procs.Count == 0)
        {
            var none = UiStrings.Get("Cleanup_NoSafe");
            Status = none;
            return none;
        }
        var r = await Task.Run(() => Cleanup.TrimWorkingSets(procs)).ConfigureAwait(true);
        ReloadAudit();
        await Task.Delay(400).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
        var msg = $"已压缩 {r.Trimmed} 个低负载进程的工作集（{r.Failed} 个失败，跳过 {all.Count - procs.Count} 个工作/游戏/前台进程），系统可用内存 +{r.FreedBytes / 1048576.0:F0} MB";
        _lastBatchTrimUtc = DateTime.UtcNow;
        Status = msg;
        return msg;
    }

    /// <summary>垃圾驻守清扫计划（Bloatware + 宿主已退出的孤儿残留，硬安全边界见 JunkSweeper）。</summary>
    public List<JunkSweepItem> BuildJunkSweepPlan(bool orphansOnly = false)
    {
        var procs = _lastProcs.ToList();
        var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
        return JunkSweeper.BuildPlan(procs, Engine.LastForegroundPid, audio,
            ConfigStore.Config.Whitelist, ConfigStore.Config.MinProcessAgeSeconds, orphansOnly);
    }

    /// <summary>执行垃圾驻守清扫计划，返回摘要。</summary>
    public async Task<string> ExecuteJunkSweepAsync(List<JunkSweepItem> plan)
    {
        int kills = 0, trims = 0, failed = 0;
        if (plan.Count > 0)
        {
            await Task.Run(() =>
            {
                foreach (var item in plan)
                {
                    var r = Cleanup.Execute(item.Process, item.Action, ConfigStore.Config.GracefulCloseTimeoutSeconds);
                    if (!r.Success) failed++;
                    else if (item.Action == CleanupActionLevel.L4ForceKill) kills++;
                    else trims++;
                }
            }).ConfigureAwait(true);
        }
        ReloadAudit();
        await Task.Delay(400).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
        var summary = plan.Count == 0
            ? UiStrings.Get("Cleanup_NoJunk")
            : $"垃圾驻守清扫：结束 {kills} 个，回收工作集 {trims} 个，失败 {failed} 个";
        Status = summary;
        return summary;
    }

    /// <summary>批量处置候选：levelOverride 为 null 时按各自建议级别执行。</summary>
    public async Task<(int done, int failed, string summary)> BatchCleanupAsync(
        IReadOnlyList<UnusedCandidate> cands, CleanupActionLevel? levelOverride = null)
    {
        int done = 0, failed = 0;
        await Task.Run(() =>
        {
            foreach (var c in cands)
            {
                var level = levelOverride ?? c.SuggestedLevel;
                var r = Cleanup.Execute(c.Process, level, ConfigStore.Config.GracefulCloseTimeoutSeconds, escalateToKill: false);
                if (r.Success) done++; else failed++;
            }
        }).ConfigureAwait(true);
        ReloadAudit();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            DisposalRecords.Clear();
            foreach (var r in Cleanup.Disposals.All()) DisposalRecords.Add(r);
        });
        await Task.Delay(600).ConfigureAwait(true);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SysMem = ProcessService.GetSystemMemory());
        var summary = $"批量处置完成：成功 {done} 个，失败 {failed} 个";
        Status = summary;
        return (done, failed, summary);
    }

    public void ResumeSuspended(SuspendRecord rec)
    {
        Cleanup.Resume(rec.Pid, rec.Name);
        ReloadAudit();
        SuspendedProcesses.Remove(rec);
    }

    public void ResumeAllSuspended()
    {
        foreach (var rec in Cleanup.Suspended.LiveRecords())
            Cleanup.Resume(rec.Pid, rec.Name);
        ReloadAudit();
        SuspendedProcesses.Clear();
    }

    /// <summary>Relaunch a previously closed/killed exe (the closest thing to an L4 undo).</summary>
    public bool RestartDisposal(DisposalRecord rec)
    {
        try
        {
            if (string.IsNullOrEmpty(rec.ExePath) || !File.Exists(rec.ExePath)) return false;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = rec.ExePath,
                WorkingDirectory = Path.GetDirectoryName(rec.ExePath) ?? "",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch { return false; }
    }
}
