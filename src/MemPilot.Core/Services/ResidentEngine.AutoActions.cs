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
                        Reason = skip,
                        Code = "auto_cleanup.skipped",
                        Args = new[] { p.Name, skip }
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
                        Reason = $"模式[{profileName}]清理名单命中，有窗口；结果: {r.Message}",
                        Code = "auto_cleanup.close",
                        Args = new[] { target.Name, profileName, r.Message }
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
                    Reason = $"模式[{profile.Name}]清理名单命中",
                    Code = "auto_cleanup.trim",
                    Args = new[] { p.Name, profile.Name }
                });
            }
        }
        if (_lastAutoActionUtc.Count > 2048) _lastAutoActionUtc.Clear();
        if (_lastSkipLogUtc.Count > 2048) _lastSkipLogUtc.Clear();
    }

    // ---- 垃圾进程建议 + 定时挂起到期恢复（v0.15.0）----

    /// <summary>
    /// 每日一次：找出"整天未使用"的垃圾进程，交给用户决定挂起/结束。
    ///
    /// v0.17.0 三处修正（运行数据驱动）：
    /// ① **19:00 之后才评估**——实测午夜 00:00 触发时"今天还没用"对所有进程都成立，
    ///    天然产出 8 个候选（含用户白天会用的），且报告是"一整天没用到"就该在黄昏说。
    /// ② **每日标记持久化**（JunkAdviceStore.AdviceDayKey），重启不重复建议。
    /// ③ **交付确认**：引擎只发事件；弹卡与冷却由 VM 在真正展示时决定。
    ///    托盘模式没显示 → VM 调 RejectJunkAdvice() 回滚当日标记，窗口可见后还能再弹。
    /// </summary>
    private bool MaybeAdviseJunk(List<ProcessInfo> procs, bool force = false)
    {
        if (!_config.Config.JunkAdviceEnabled) return false;
        // force=true（设置页「立即检查」）跳过时间窗、重试冷却与当日标记，安全过滤与学习冷却照常
        if (!force && !ShouldEvaluateJunkAdvice(DateTime.Now, JunkAdviceEarliestHour,
                JunkAdvice?.AdviceDayKey, JunkAdvice?.RetryAfterUtc ?? default, UiActive, TodayAdviceDelivered()))
            return false;
        try
        {
            var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
            var advice = JunkAdvisor.Recommend(
                procs, _behaviors?.Entries ?? new Dictionary<string, ProcessBehavior>(),
                _lastForegroundPid, audio, _config.Config.Whitelist,
                _config.Config.MinProcessAgeSeconds, JunkAdvice, DateTime.UtcNow,
                MarkedCleanableProvider?.Invoke());
            if (advice == null) return false;   // 没有候选：不消费当日标记，晚些再评估
            JunkAdvice?.MarkAdviceDelivered(DateTime.Now.ToString("yyyyMMdd"));
            // v0.20.0：这里不再写决策日志——只有 VM 真正弹卡才写（见 MainViewModel.JunkAdvice.cs）。
            JunkAdvised?.Invoke(advice);
            return true;
        }
        catch (Exception ex) { Helpers.Diag.Record("junk-advice", ex); return false; }
    }

    /// <summary>建议最早评估时刻（本地小时）。19 点后"一整天没用过"才有意义。</summary>
    public const int JunkAdviceEarliestHour = 19;

    /// <summary>
    /// 是否到了评估垃圾建议的时候（v0.17.0，纯函数便于测试）：
    /// ① 窗口必须可见（v0.20.0）——托盘模式不评估、不发事件，从源头掐掉每 tick 空转；
    /// ② 本地时间 ≥ earliestHour（默认 19 点——午夜评估等于把所有进程都算"今天未使用"）；
    /// ③ 当天尚未交付（deliveredDayKey 持久化在 junk-advice.json，跨重启有效）；
    /// ④ 重试冷却已过期（v0.20.0：交付失败后 15 分钟内不再评估，避免每 tick 空转）；
    /// ⑤ 另一种建议今天没交付过（v0.21.0：垃圾建议与长驻未用建议共用一张卡片位）。
    /// </summary>
    public static bool ShouldEvaluateJunkAdvice(DateTime nowLocal, int earliestHour, string? deliveredDayKey,
        DateTime retryAfterUtc = default, bool uiActive = true, bool anotherAdviceDeliveredToday = false)
    {
        if (!uiActive) return false;
        if (anotherAdviceDeliveredToday) return false;
        if (nowLocal.Hour < earliestHour) return false;
        if (retryAfterUtc != default && retryAfterUtc > nowLocal.ToUniversalTime()) return false;
        if (string.Equals(deliveredDayKey, nowLocal.ToString("yyyyMMdd"), StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>
    /// 今天是否已经交付过（仍在启用的）建议（v0.21.0）：每天最多一张建议卡。
    /// 按开关过滤——用户关掉垃圾建议后，它今天的历史标记不该阻塞长驻建议。
    /// </summary>
    private bool TodayAdviceDelivered()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var cfg = _config.Config;
        return (cfg.JunkAdviceEnabled && string.Equals(JunkAdvice?.AdviceDayKey, today, StringComparison.Ordinal))
               || (cfg.LongResidentAdviceEnabled && string.Equals(JunkAdvice?.LongResidentDayKey, today, StringComparison.Ordinal));
    }

    /// <summary>交付失败后的默认重试冷却（v0.20.0）。15 分钟：够用户回到电脑，又不至于整天不再提。</summary>
    public static readonly TimeSpan JunkAdviceRetryCooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// VM 报告"没能交付"（窗口不可见）：回滚当日标记并设 15 分钟冷却（v0.20.0）。
    /// 不烧任何进程的 7 天冷却——冷却只在真正弹卡时由 VM 写入。
    /// </summary>
    public void DeferJunkAdvice(TimeSpan cooldown)
    {
        try { JunkAdvice?.DeferDelivery(cooldown); }
        catch (Exception ex) { Helpers.Diag.Record("junk-advice-defer", ex); }
    }

    /// <summary>旧入口（v0.17.0）：回滚当日标记，下一次 tick 立即可重试。v0.20.0 起委托给 DeferJunkAdvice。</summary>
    public void RejectJunkAdvice() => DeferJunkAdvice(JunkAdviceRetryCooldown);

    /// <summary>设置页「立即检查」：忽略时间窗与当日标记，安全过滤与冷却照常。</summary>
    public void ForceJunkAdviceNow(List<ProcessInfo> procs)
    {
        try { JunkAdvice?.ClearAdviceDay(); } catch { }
        bool delivered = MaybeAdviseJunk(procs, force: true);
        if (!delivered)
            // v0.21.0：垃圾无候选时再试一次长驻未用（用户开了开关就能立刻验证有没有候选）
            delivered = MaybeAdviseLongResident(procs, force: true);
        if (!delivered)
        {
            // v0.17.0：立即检查无果也要留痕——否则用户点了按钮"没反应"，无法判断是没候选还是坏了
            _decisions.Log(new DecisionEntry
            {
                Kind = "JunkAdvice",
                Target = "-",
                Decision = DecisionLogFilter.DecisionNoCandidate,
                Reason = "立即检查：没有符合条件的目标（今日有活动 / 当前有窗口 / 运行时长不足 / 学习中冷却）；"
                         + "长驻未用建议需在设置里开启",
                Code = "junk_advice.no_candidate",
                Args = Array.Empty<string>()
            });
        }
    }

    // ---- 长驻未用建议（v0.21.0，默认关闭）----

    /// <summary>
    /// 每天一次：找出"跑了一整天、一次都没用过、但占着大内存"的非垃圾进程，**只建议挂起**。
    /// 数据依据（11-23 复盘）：vmmemWSL / ArmouryCrate / MSPC 这类进程没有垃圾证据，
    /// JunkAdvisor 覆盖不到，但它们确实长期占用内存；挂起 2/8 小时是零成本的回收。
    /// 与垃圾建议共用每日一次 + 19 点后 + 15 分钟冷却 + 学习账本，且两者互斥交付。
    /// </summary>
    private bool MaybeAdviseLongResident(List<ProcessInfo> procs, bool force = false)
    {
        if (!_config.Config.LongResidentAdviceEnabled) return false;
        if (!force && !ShouldEvaluateJunkAdvice(DateTime.Now, JunkAdviceEarliestHour,
                JunkAdvice?.LongResidentDayKey, JunkAdvice?.RetryAfterUtc ?? default, UiActive, TodayAdviceDelivered()))
            return false;
        try
        {
            var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
            var advice = LongResidentAdvisor.Recommend(
                procs, _behaviors?.Entries ?? new Dictionary<string, ProcessBehavior>(),
                _lastForegroundPid, audio, _config.Config.Whitelist, JunkAdvice, DateTime.UtcNow);
            if (advice == null) return false;   // 没有候选：不消费当日标记
            JunkAdvice?.MarkLongResidentDelivered(DateTime.Now.ToString("yyyyMMdd"));
            JunkAdvised?.Invoke(advice);
            return true;
        }
        catch (Exception ex) { Helpers.Diag.Record("long-resident-advice", ex); return false; }
    }

    // ---- WSL 常驻提示（v0.22.0，默认开启）----

    /// <summary>
    /// 每天一次：vmmemWSL 占用 ≥ 阈值且今天零活动 → 提示可以 `wsl --shutdown`。
    /// 与垃圾/长驻建议**不互斥**：vmmem 的正确动作是退出 WSL（不是挂起/结束），
    /// 这条提示不占"每天一张建议卡"的名额。
    /// 硬约束：只发事件，不执行任何动作（VM 只提供复制命令）。
    /// </summary>
    private bool MaybeAdviseWsl(List<ProcessInfo> procs)
    {
        var cfg = _config.Config;
        if (!cfg.WslShutdownHintEnabled) return false;
        if (!UiActive) return false;   // 托盘不评估（与 v0.20 的教训一致）

        var today = DateTime.Now.ToString("yyyyMMdd");
        if (string.Equals(JunkAdvice?.WslHintDayKey, today, StringComparison.Ordinal)) return false;

        try
        {
            var hint = WslHintAdvisor.Recommend(procs, cfg.WslShutdownHintMinMb * 1048576L,
                cfg.WslShutdownHintMinAgeMinutes, DateTime.Now);
            if (hint == null) return false;
            // 学习降噪：用户忽略过就 7/30/90 天不再提（与垃圾建议共用账本）
            if (JunkAdvice != null && !JunkAdvice.ShouldSuggest(hint.Process.Name, DateTime.UtcNow)) return false;
            JunkAdvice?.MarkWslHintDelivered(today);
            WslHintAdvised?.Invoke(hint);
            return true;
        }
        catch (Exception ex) { Helpers.Diag.Record("wsl-hint", ex); return false; }
    }

    /// <summary>定时挂起到期自动恢复（每 tick 检查；Start 时也补跑一次覆盖关闭期间到期）。</summary>
    internal void ResumeExpiredSuspends()
    {
        try
        {
            var due = _cleanup.Suspended.TakeExpired(DateTime.UtcNow);
            foreach (var rec in due)
            {
                var r = _cleanup.ResumeRecord(rec);
                // v0.17.0：恢复失败时**重新挂回**（5 分钟后重试）——旧实现先删记录再恢复，
                // 失败后记录消失，进程永久留在挂起状态且无重试路径。
                // 进程已退出/PID 复用的（ResumeRecord 的失败文案）不重试。
                bool retry = !r.Success && CleanupEngine.IsAliveSame(rec.Pid, rec.StartTime);
                if (retry)
                    _cleanup.Suspended.Add(rec.Pid, rec.StartTime, rec.Name,
                        rec.Reason + "（恢复失败，5 分钟后重试）", DateTime.UtcNow.AddMinutes(5));
                _decisions.Log(new DecisionEntry
                {
                    Kind = "JunkAdvice",
                    Target = $"{rec.Name} (PID {rec.Pid})",
                    Decision = r.Success ? "自动恢复" : (retry ? "恢复重试" : "恢复取消"),
                    Reason = r.Success
                        ? $"定时挂起到期，已自动恢复（挂起于 {rec.SuspendedAtUtc.ToLocalTime():HH:mm}）"
                        : (retry ? $"定时挂起到期但恢复失败：{r.Message}；已安排 5 分钟后重试"
                                 : $"定时挂起到期但{r.Message}，已清除记录"),
                    Code = r.Success ? "junk_advice.resume_ok" : (retry ? "junk_advice.resume_retry" : "junk_advice.resume_cancel"),
                    Args = r.Success
                        ? new[] { rec.Name, rec.SuspendedAtUtc.ToLocalTime().ToString("HH:mm") }
                        : new[] { rec.Name, r.Message }
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
                    Reason = $"[证据:{item.Evidence}] {item.Reason} → {item.Action}: {r.Message}",
                    Code = r.Success ? "junk_sweep.done" : "junk_sweep.failed",
                    Args = new[] { item.Process.Name, item.Action.ToString(), r.Message }
                });
            }
            if (_lastJunkSweepUtc.Count > 2048) _lastJunkSweepUtc.Clear();
        }
        catch { }
    }
}
