using System.Diagnostics;
using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>自动动作：模式驱动的清理、系统列表优化、垃圾常驻清扫。</summary>
public partial class ResidentEngine
{

    // ---- per-process auto cleanup (mode CleanPatterns), capped at L1 trim / L3 close ----

    private void AutoCleanup(List<ProcessInfo> procs)
    {
        var cfg = _config.Config;
        var profile = cfg.ModeProfiles.TryGetValue(cfg.CurrentMode, out var pr)
            ? pr
            : new ModeProfile { Mode = cfg.CurrentMode, Name = cfg.CurrentMode.ToString() };
        if (profile.CleanPatterns.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var p in procs)
        {
            bool matches = profile.CleanPatterns.Any(pat =>
                p.Name.Contains(pat, StringComparison.OrdinalIgnoreCase) ||
                p.ExecutablePath.Contains(pat, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

            // evaluate guards in order; the first hit explains the skip (logged at most once/10min/pid)
            string? skip =
                (p.IsProtected || p.IsSystemProcess || p.IsService) ? "系统/服务/受保护" :
                p.IsWhitelisted ? "白名单" :
                p.ModeKeep ? "模式 KeepPattern 命中" :
                p.Category is ProcessCategory.Unknown or ProcessCategory.SystemCritical
                    or ProcessCategory.SecuritySoftware or ProcessCategory.AntiCheat
                    or ProcessCategory.Driver ? $"分类不可自动处置 ({p.Category})" :
                p.Pid == _lastForegroundPid ? "前台进程" :
                p.AudioActive ? "正在发声" :
                p.IsSuspended ? "已被挂起" :
                (p.StartTime != DateTime.MinValue &&
                 (DateTime.Now - p.StartTime).TotalSeconds < cfg.MinProcessAgeSeconds)
                    ? $"最小存活期 ({cfg.MinProcessAgeSeconds}s)" : null;

            if (skip != null)
            {
                if (!_lastSkipLogUtc.TryGetValue(p.Pid, out var lastSkip) || (now - lastSkip).TotalMinutes >= 10)
                {
                    _lastSkipLogUtc[p.Pid] = now;
                    _decisions.Log(new DecisionEntry
                    {
                        Kind = "AutoCleanup",
                        Target = $"{p.Name} (PID {p.Pid})",
                        Decision = "跳过",
                        Reason = skip
                    });
                }
                continue;
            }

            // per-process cooldown: at most one auto action per 10 minutes
            if (_lastAutoActionUtc.TryGetValue(p.Pid, out var last) && (now - last).TotalMinutes < 10) continue;

            if (cfg.AutoCloseCleanMatches && p.HasVisibleWindow)
            {
                // v0.9.1（复盘 P1）：L3 会内部 Thread.Sleep 轮询到 gracefulTimeout（默认 8s/个），
                // 在 tick 线程同步执行 = N×8s 的刷新停摆，期间全屏/压力守卫全部失明。
                // 改投递到线程池，本 tick 只记账"已提交"。
                _lastAutoActionUtc[p.Pid] = now;
                var target = p;
                var profileName = profile.Name;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    var r = _cleanup.Execute(target, CleanupActionLevel.L3GracefulClose,
                        _config.Config.GracefulCloseTimeoutSeconds, escalateToKill: false);
                    _decisions.Log(new DecisionEntry
                    {
                        Kind = "AutoCleanup",
                        Target = $"{target.Name} (PID {target.Pid})",
                        Decision = "执行 L3 优雅关闭",
                        Reason = $"模式[{profileName}]清理名单命中，有窗口；结果: {r.Message}"
                    });
                });
            }
            else
            {
                _cleanup.Execute(p, CleanupActionLevel.L1Trim, 0);
                _lastAutoActionUtc[p.Pid] = now;
                _decisions.Log(new DecisionEntry
                {
                    Kind = "AutoCleanup",
                    Target = $"{p.Name} (PID {p.Pid})",
                    Decision = "执行 L1 回收工作集",
                    Reason = $"模式[{profile.Name}]清理名单命中"
                });
            }
        }
        if (_lastAutoActionUtc.Count > 2048) _lastAutoActionUtc.Clear();
        if (_lastSkipLogUtc.Count > 2048) _lastSkipLogUtc.Clear();
    }

    // ---- 垃圾进程建议 + 定时挂起到期恢复（v0.15.0）----

    /// <summary>
    /// 每日一次：找出"整天未使用"的垃圾进程，交给用户决定挂起/结束。
    /// 只发事件不执行任何动作；用户不在场（托盘）时由 VM 记录后跳过。
    /// </summary>
    private void MaybeAdviseJunk(List<ProcessInfo> procs)
    {
        if (!_config.Config.JunkAdviceEnabled) return;
        var today = DateTime.Now.Date;
        if (_lastJunkAdviceDay == today) return;          // 一天最多一次
        _lastJunkAdviceDay = today;
        try
        {
            var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
            var advice = JunkAdvisor.Recommend(
                procs, _behaviors?.Entries ?? new Dictionary<string, ProcessBehavior>(),
                _lastForegroundPid, audio, _config.Config.Whitelist,
                _config.Config.MinProcessAgeSeconds, JunkAdvice, DateTime.UtcNow,
                MarkedCleanableProvider?.Invoke());
            if (advice == null) return;
            JunkAdvice?.RecordSuggested(advice.Candidates[0].Name, DateTime.UtcNow);
            foreach (var p in advice.Candidates) JunkAdvice?.RecordSuggested(p.Name, DateTime.UtcNow);
            _decisions.Log(new DecisionEntry
            {
                Kind = "JunkAdvice",
                Target = advice.NamesText,
                Decision = "建议",
                Reason = advice.Reason + $"；预计释放 {advice.TotalPrivateBytes / 1048576.0:F0} MB"
            });
            JunkAdvised?.Invoke(advice);
        }
        catch (Exception ex) { Helpers.Diag.Record("junk-advice", ex); }
    }

    /// <summary>定时挂起到期自动恢复（每 tick 检查；Start 时也补跑一次覆盖关闭期间到期）。</summary>
    internal void ResumeExpiredSuspends()
    {
        try
        {
            var due = _cleanup.Suspended.TakeExpired(DateTime.UtcNow);
            foreach (var rec in due)
            {
                var r = _cleanup.Resume(rec.Pid, rec.Name);
                _decisions.Log(new DecisionEntry
                {
                    Kind = "JunkAdvice",
                    Target = $"{rec.Name} (PID {rec.Pid})",
                    Decision = "自动恢复",
                    Reason = r.Success
                        ? $"定时挂起到期，已自动恢复（挂起于 {rec.SuspendedAtUtc.ToLocalTime():HH:mm}）"
                        : $"定时挂起到期但恢复失败：{r.Message}"
                });
            }
        }
        catch (Exception ex) { Helpers.Diag.Record("junk-resume", ex); }
    }

    // ---- junk resident sweep (auto mode, opt-in) ----

    private void AutoJunkSweep(List<ProcessInfo> procs)
    {
        try
        {
            if (ForegroundFullscreen) return; // 游戏/全屏时不动作，与提交兜底一致
            var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
            var plan = JunkSweeper.BuildPlan(procs, _lastForegroundPid, audio,
                _config.Config.Whitelist, _config.Config.MinProcessAgeSeconds,
                _config.Config.AutoJunkSweepOrphansOnly);
            var now = DateTime.UtcNow;
            foreach (var item in plan)
            {
                var key = item.Process.StateKey;
                if (_lastJunkSweepUtc.TryGetValue(key, out var last) && (now - last).TotalMinutes < 30) continue;
                _lastJunkSweepUtc[key] = now;
                var r = _cleanup.Execute(item.Process, item.Action, _config.Config.GracefulCloseTimeoutSeconds);
                _decisions.Log(new DecisionEntry
                {
                    Kind = "JunkSweep",
                    Target = $"{item.Process.Name} (PID {item.Process.Pid})",
                    Decision = r.Success ? "已处置" : "失败",
                    Reason = $"[证据:{item.Evidence}] {item.Reason} → {item.Action}: {r.Message}"
                });
            }
            if (_lastJunkSweepUtc.Count > 2048) _lastJunkSweepUtc.Clear();
        }
        catch { }
    }
}
