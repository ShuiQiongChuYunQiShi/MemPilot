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
                var r = _cleanup.Execute(p, CleanupActionLevel.L3GracefulClose, cfg.GracefulCloseTimeoutSeconds, escalateToKill: false);
                _lastAutoActionUtc[p.Pid] = now;
                _decisions.Log(new DecisionEntry
                {
                    Kind = "AutoCleanup",
                    Target = $"{p.Name} (PID {p.Pid})",
                    Decision = "执行 L3 优雅关闭",
                    Reason = $"模式[{profile.Name}]清理名单命中，有窗口；结果: {r.Message}"
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

    // ---- system-list auto optimization, pressure/interval gated, fullscreen-aware ----

    private int _optimizeLowYieldStreak;
    private int _optimizeCooldownMinutes = 10;
    private DateTime _lastOptimizeSkipLogUtc = DateTime.MinValue;

    private void AutoOptimize(SystemMemoryInfo mem)
    {
        var cfg = _config.Config;
        double freePct = mem.TotalPhysicalBytes <= 0 ? 100 : (double)mem.AvailablePhysicalBytes / mem.TotalPhysicalBytes * 100;
        bool lowMemory = cfg.AutoOptimizeOnLowMemory && freePct < cfg.AutoOptimizeThresholdPercent;
        bool intervalDue = cfg.AutoOptimizeIntervalEnabled &&
                           (DateTime.UtcNow - _lastAutoOptimizeUtc).TotalMinutes >= cfg.AutoOptimizeIntervalMinutes;

        if (cfg.SkipOptimizeInFullscreen && ForegroundFullscreen)
        {
            // v0.5.0：跳过日志也节流，否则游戏期间每个 tick 刷一条（数据里 115 条跳过全是这个）
            if ((lowMemory || intervalDue) && (DateTime.UtcNow - _lastOptimizeSkipLogUtc).TotalMinutes >= 10)
            {
                _lastOptimizeSkipLogUtc = DateTime.UtcNow;
                _decisions.Log(new DecisionEntry
                {
                    Kind = "AutoOptimize",
                    Target = "-",
                    Decision = "跳过",
                    Reason = $"前台全屏（游戏）时禁用系统级清理（可用 {freePct:F0}%）"
                });
            }
            return;
        }
        if (!lowMemory && !intervalDue) return;

        // v0.5.0：冷却——之前没有冷却，可用% 在阈值附近抖动时会连发（实测 6 分钟 34 次、13 分钟 58 次）
        var cooldown = TimeSpan.FromMinutes(_optimizeCooldownMinutes);
        if (_lastAutoOptimizeUtc != DateTime.MinValue && DateTime.UtcNow - _lastAutoOptimizeUtc < cooldown)
            return;

        _lastAutoOptimizeUtc = DateTime.UtcNow;
        long before = mem.AvailablePhysicalBytes;
        // low-priority standby purge only: full standby purge / system file cache trimming
        // actively hurt running games (texture caches live in standby)
        _memCleaner.CleanAreas(new List<MemoryArea> { MemoryArea.StandbyListLowPriority });
        // v0.5.0：真实测一次 after——旧代码把同一个 mem 值写成 before/after，198 次执行全是"零收益"
        long after = _probe.GetSystemMemory().AvailablePhysicalBytes;
        long delta = Math.Max(0, after - before);
        var trigger = lowMemory ? $"低内存触发（可用 {freePct:F0}%）" : "周期触发";

        if (delta < 100L * 1024 * 1024) _optimizeLowYieldStreak++;
        else _optimizeLowYieldStreak = 0;
        if (_optimizeLowYieldStreak >= 5 && _optimizeCooldownMinutes < 30)
        {
            _optimizeCooldownMinutes = 30;
            _optimizeLowYieldStreak = 0;
            _decisions.Log(new DecisionEntry
            {
                Kind = "AutoOptimize",
                Target = "-",
                Decision = "降频",
                Reason = "连续 5 次清理收益 <100 MB（备用列表本就多为可回收缓存）——冷却从 10 分钟延长到 30 分钟"
            });
        }

        _audit.Log(new AuditEntry
        {
            Action = "AutoOptimize",
            Target = "StandbyListLowPriority",
            Level = "Auto",
            Result = $"{trigger}；实测释放 {delta / 1048576.0:F0} MB",
            MemoryBeforeBytes = before,
            MemoryAfterBytes = after
        });
        _decisions.Log(new DecisionEntry
        {
            Kind = "AutoOptimize",
            Target = "StandbyListLowPriority",
            Decision = "执行",
            Reason = $"{trigger}；实测可用 +{delta / 1048576.0:F0} MB（冷却 {_optimizeCooldownMinutes} 分钟）"
        });
    }

    // ---- junk resident sweep (auto mode, opt-in) ----

    private void AutoJunkSweep(List<ProcessInfo> procs)
    {
        try
        {
            if (ForegroundFullscreen) return; // 游戏/全屏时不动作，与 AutoOptimize 一致
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
