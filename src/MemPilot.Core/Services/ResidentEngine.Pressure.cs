using System.Diagnostics;
using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>压力观测与提醒：内存增长、提交压力（CommitWatch）、时段画像提醒。</summary>
public partial class ResidentEngine
{

    // ---- 提交内存兜底（v0.5.0）----
    private readonly ProcessService _probe = new();
    private DateTime _lastReliefUtc = DateTime.MinValue;
    private ReliefAction _lastReliefAction = ReliefAction.None;

    /// <summary>AI 会话是否活跃（由 VM 从 GpuMonitor 会话事件同步过来）；活跃时不做任何压缩。</summary>
    public bool AiSessionActive { get; set; }

    /// <summary>提交告急且允许 L3 时，向 UI 请求确认（候选进程, 预计释放提交字节）。UI 决定是否关闭。</summary>
    public event Action<IReadOnlyList<ProcessInfo>, long>? CommitReliefL3Suggested;

    /// <summary>
    /// 提交兜底：数据依据是 11-01 02:30（提交余量 0.3%、物理可用仍 31%）——只看可用% 的旧逻辑不会动手。
    /// 一级清备用列表（零进程风险）；2 分钟后仍 &lt;5% 才降级到"安全子集"压缩（TrimSelection）。
    /// </summary>
    private void WatchCommitRelief(List<ProcessInfo> procs, SystemMemoryInfo mem)
    {
        if (!_config.Config.AutoCommitReliefEnabled) return;
        double limitGb = mem.TotalCommitLimitBytes / 1073741824.0;
        if (limitGb <= 0 || _commitWindow.Count < 2) return;

        double commitFreeGb = (mem.TotalCommitLimitBytes - mem.TotalCommittedBytes) / 1073741824.0;
        double commitFreePct = commitFreeGb / limitGb * 100;
        double freeGb = mem.AvailablePhysicalBytes / 1073741824.0;
        double growthGb = Math.Max(0, (mem.TotalCommittedBytes - _commitWindow[0].Commit) / 1073741824.0);

        bool gameBusy = procs.Any(p =>
            p.Category is ProcessCategory.Game or ProcessCategory.GameLauncher or ProcessCategory.AntiCheat &&
            (p.Memory.CpuPercent > 5 || LastGpuPercent.GetValueOrDefault(p.Pid) > 10));

        var action = CommitReliefPolicy.Decide(new ReliefInputs
        {
            CommitFreePct = commitFreePct,
            CommitGrowthGb = growthGb,
            FreeGb = freeGb,
            Confirmed = _commitWindow.Count >= 2,
            NowUtc = DateTime.UtcNow,
            LastActionUtc = _lastReliefUtc,
            LastAction = _lastReliefAction,
            ForegroundFullscreen = ForegroundFullscreen,
            AiSessionActive = AiSessionActive,
            GameBusy = gameBusy,
            AllowL3 = _config.Config.CommitReliefL3Enabled
        });
        if (action == ReliefAction.None) return;

        long before = mem.AvailablePhysicalBytes;
        var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
        var safeSet = TrimSelection.BuildSafeTrimSet(procs, _lastForegroundPid, audio);

        // v0.10.0（开源前正确性修复）：**先判权限，再说执行**。
        // v0.13.0（第四轮复盘）：**失败不吃冷却**——旧实现 `_lastReliefUtc` 无条件赋值，
        // 一次失败的 PurgeStandby 占住 2 分钟冷却、10 分钟重复周期，救援梯被死动作堵住
        // （实测 11-04 15:53 起连续 4 次 0xC0000061 失败期间，TrimSafe 一次都没跟上）。
        // 现在失败 → 本 tick 立即落到 TrimSafe；只有有正收益的动作才吃冷却。
        string decision;
        string detail;
        string level;
        bool reliefDelivered = false;
        if (action == ReliefAction.PurgeStandby && !_memCleaner.IsAdmin())
        {
            level = "L0Purge";
            decision = "跳过";
            detail = $"清理低优先级备用列表需要管理员权限（当前非提权），未执行"
                     + $"；提交余量 {commitFreePct:F1}%，可用 {freeGb:F1} GB，10 分钟增长 {growthGb:F1} GB";
        }
        else if (action == ReliefAction.PurgeStandby)
        {
            level = "L0Purge";
            var purge = _memCleaner.CleanAreas(new List<MemoryArea> { MemoryArea.StandbyListLowPriority });
            bool ok = purge.TryGetValue(MemoryArea.StandbyListLowPriority, out var r1) && r1.success;
            decision = ok ? "执行" : "失败";
            reliefDelivered = ok;
            detail = ok
                ? $"清理低优先级备用列表（提交余量 {commitFreePct:F1}%，可用 {freeGb:F1} GB，10 分钟增长 {growthGb:F1} GB）"
                : $"清理低优先级备用列表失败：{(r1.message ?? "未知原因")}（提交余量 {commitFreePct:F1}%，可用 {freeGb:F1} GB）";
            if (!ok)
            {
                // 失败立即升级 TrimSafe（数据依据：TrimSafe 4/4 生效全持续 >5min；PurgeStandby 9 成功里 8 次 0MB）
                var rEsc = _cleanup.TrimWorkingSets(safeSet);
                detail += $"；已立即升级安全子集压缩 {rEsc.Trimmed} 个（物理可用 +{rEsc.FreedBytes / 1048576.0:F0} MB）";
                level = "L0Purge+L1Trim";
                reliefDelivered = rEsc.Trimmed > 0;
            }
        }
        else if (action == ReliefAction.RequestL3 && CommitReliefL3Suggested != null)
        {
            // 唯一能真正降低提交计数的动作：结束闲置后台。候选限制 8 个、只来自安全子集。
            // v0.13.0：候选必须有可见窗口——无窗口进程对 WM_CLOSE 是空操作
            // （实测 11-05 05:25 关闭 wps/wetype_update/lghub_updater 全部 Exited:false）。
            var candidates = safeSet.Where(p => p.HasVisibleWindow).Take(8).ToList();
            long reclaim = candidates.Sum(p => p.Memory.PrivateBytes);
            if (candidates.Count == 0)
            {
                level = "L1Trim";
                var r0 = _cleanup.TrimWorkingSets(safeSet);
                decision = "执行";
                reliefDelivered = r0.Trimmed > 0;
                detail = $"没有可优雅关闭的闲置后台（候选均无可见窗口），退回安全子集压缩 {r0.Trimmed} 个（物理可用 +{r0.FreedBytes / 1048576.0:F0} MB；注意：压缩不降提交）";
            }
            else
            {
                level = "L3Request";
                decision = "请求确认";
                detail = $"请求确认：优雅关闭 {candidates.Count} 个闲置后台（预计释放提交 {reclaim / 1048576.0:F0} MB；提交余量 {commitFreePct:F1}%）";
                CommitReliefL3Suggested(candidates, reclaim);
            }
        }
        else
        {
            level = "L1Trim";
            var r = _cleanup.TrimWorkingSets(safeSet);
            decision = "执行";
            reliefDelivered = r.Trimmed > 0;
            detail = $"安全子集压缩 {r.Trimmed} 个低负载进程（排除 {procs.Count - safeSet.Count} 个：工作/游戏/前台/忙碌/系统），回收 {r.FreedBytes / 1048576.0:F0} MB（仅物理，不降提交）";
        }

        var after = _probe.GetSystemMemory();
        long delta = Math.Max(0, after.AvailablePhysicalBytes - before);
        if (reliefDelivered)
        {
            _lastReliefUtc = DateTime.UtcNow;
            _lastReliefAction = action;
        }
        _audit.Log(new AuditEntry
        {
            Action = "CommitRelief",
            Target = action.ToString(),
            Level = level,
            Result = detail,
            MemoryBeforeBytes = before,
            MemoryAfterBytes = after.AvailablePhysicalBytes,
            Details = $"提交余量 {commitFreePct:F1}% → 可用 {freeGb:F1} GB；实测释放 {delta / 1048576.0:F0} MB"
        });
        _decisions.Log(new DecisionEntry
        {
            Kind = "CommitRelief",
            Target = action.ToString(),
            Decision = decision,
            Reason = detail + (decision == "执行" ? $"；实测可用 +{delta / 1048576.0:F0} MB" : "")
        });
    }

    // ---- memory growth watch (leak-ish behavior lands in the decision log, never in actions) ----

    // ---- 低可用兜底（v0.13.0，四轮复盘 P1）----
    private DateTime _lastFloorTrimUtc = DateTime.MinValue;

    /// <summary>
    /// 数据依据：11 天效果审计——L1TrimAll 29/29 成功、中位回收 1924 MB、大额全兑现且 >5min 持续；
    /// 而 11-04 15:53（可用 509MB）与 11-05 05:24（1.94GB）两个危机时刻，救援梯被失败的
    /// PurgeStandby 堵住，什么都没做。本触发器独立于 CommitRelief 状态机：
    /// **可用 <3GB 或 提交余量 <10%**（单采样确认，间隔 10 分钟去重）→ 直接执行安全子集压缩。
    /// 前台全屏跳过（与 AutoOptimize/CommitRelief 的守门一致）。
    /// </summary>
    private void WatchAvailableFloor(List<ProcessInfo> procs, SystemMemoryInfo mem)
    {
        double availGb = mem.AvailablePhysicalBytes / 1073741824.0;
        double commitFreeGb = (mem.TotalCommitLimitBytes - mem.TotalCommittedBytes) / 1073741824.0;
        double limitGb = mem.TotalCommitLimitBytes / 1073741824.0;
        bool floorHit = availGb < 3.0 || (limitGb > 0 && commitFreeGb / limitGb * 100 < 10);
        if (!floorHit) return;
        if (ForegroundFullscreen) return;                       // 游戏/全屏不动作
        if ((DateTime.UtcNow - _lastFloorTrimUtc).TotalMinutes < 10) return;   // 去重

        var audio = new HashSet<int>(procs.Where(p => p.AudioActive).Select(p => p.Pid));
        var safeSet = TrimSelection.BuildSafeTrimSet(procs, _lastForegroundPid, audio);
        if (safeSet.Count == 0) return;

        long before = mem.AvailablePhysicalBytes;
        var r = _cleanup.TrimWorkingSets(safeSet);
        _lastFloorTrimUtc = DateTime.UtcNow;
        _lastReliefUtc = DateTime.UtcNow;       // 同时压制 CommitRelief 在同一时刻重复触发
        _lastReliefAction = ReliefAction.TrimSafe;
        var after = _probe.GetSystemMemory();
        long delta = Math.Max(0, after.AvailablePhysicalBytes - before);

        string trigger = availGb < 3.0 ? $"可用 {availGb:F1} GB < 3 GB" : $"提交余量 <10%";
        _audit.Log(new AuditEntry
        {
            Action = "CommitRelief",
            Target = "FloorTrim",
            Level = "L1Trim",
            Result = $"低可用兜底：{trigger}，安全子集压缩 {r.Trimmed} 个（排除 {procs.Count - safeSet.Count} 个），可用 +{delta / 1048576.0:F0} MB",
            MemoryBeforeBytes = before,
            MemoryAfterBytes = after.AvailablePhysicalBytes
        });
        _decisions.Log(new DecisionEntry
        {
            Kind = "CommitRelief",
            Target = "FloorTrim",
            Decision = "执行",
            Reason = $"低可用兜底触发（{trigger}）；压缩 {r.Trimmed} 个安全子集，实测可用 +{delta / 1048576.0:F0} MB"
        });
    }


    private void WatchMemoryGrowth(List<ProcessInfo> procs)
    {
        const long ThresholdBytes = 800L * 1024 * 1024;   // v0.13.0：512→800MB（23 条/天多为常态波动，无一被跟进）
        var now = DateTime.UtcNow;
        foreach (var p in procs)
        {
            if (p.MemoryGrowthBytes < ThresholdBytes) continue;
            // v0.9.0 降噪：同一进程每天最多 1 条（实测 290 条 GrowthWatch 里同一 PID 重复 18 次，
            // 且从不动作——它只是提示）。想看增长趋势请去历史页。
            if (_lastGrowthLogUtc.TryGetValue(p.StateKey, out var last) && (now - last).TotalHours < 48) continue;   // v0.13.0：1 天→2 天
            _lastGrowthLogUtc[p.StateKey] = now;
            _decisions.Log(new DecisionEntry
            {
                Kind = "GrowthWatch",
                Target = $"{p.Name} (PID {p.Pid})",
                Decision = "提示",
                Reason = $"工作集在观察窗口内增长 {p.MemoryGrowthBytes / 1048576.0:F0} MB（仅提示，不自动处置）"
            });
        }
        if (_lastGrowthLogUtc.Count > 2048) _lastGrowthLogUtc.Clear();
    }

    // ---- system commit pressure watch ----
    // Model loading (ollama etc.) lives in commit/pagefile, not working set: on 2026-10-29
    // commit jumped +22 GB in 10 minutes while every per-process WS stayed under the growth
    // threshold, so GrowthWatch missed it. This watches the system-level curve instead.
    //
    // 2026-10-31 误报修正：开机/唤醒时自启风暴会让提交内存在几分钟内涨 10-20GB（实测 07:40
    // 唤醒后 5 分钟 +18.6GB、进程 224→631），但可用内存 14GB 完全健康。增长本身不是问题，
    // 吃光余量才是。因此：① 窗口跨过 >10 分钟的空档时重置基线（睡眠恢复不放大增长率）；
    // ② 增长型告警必须同时满足"可用 <8GB 且提交余量 <25%"，否则不报；③ 文案分级。

    public enum CommitWatchLevel { None, Tight, Critical }

    /// <summary>提交内存告警判定（纯函数，可单测）。growthGb=10 分钟增量；freeGb=可用物理内存；
    /// commitFreeGb=提交余量；commitLimitGb=提交上限。</summary>
    public static CommitWatchLevel EvaluateCommitWatch(double growthGb, double freeGb, double commitFreeGb, double commitLimitGb)
    {
        if (commitLimitGb <= 0) return CommitWatchLevel.None;
        if (commitFreeGb < commitLimitGb * 0.10) return CommitWatchLevel.Critical;      // 真·余量告急
        bool surge = growthGb >= 8 && freeGb < 8 && commitFreeGb < commitLimitGb * 0.25; // 增长且真的在吃余量
        return surge ? CommitWatchLevel.Tight : CommitWatchLevel.None;
    }

    private void WatchCommitPressure(SystemMemoryInfo mem)
    {
        var now = DateTime.UtcNow;
        // 睡眠/关机恢复：上一轮采样已是 >10 分钟前 → 这是一条新基线，不能把「醒来的空闲值」
        // 当作增长起点（否则每次开机都会误报）
        if (_commitWindow.Count > 0 && (now - _commitWindow[^1].Utc).TotalMinutes > 10)
            _commitWindow.Clear();
        _commitWindow.Add((now, mem.TotalCommittedBytes, mem.AvailablePhysicalBytes, mem.TotalCommitLimitBytes));
        _commitWindow.RemoveAll(x => (now - x.Utc).TotalMinutes > 10);
        if (_commitWindow.Count < 2) return;
        if ((now - _lastCommitWatchUtc).TotalMinutes < 30) return;

        double growthGb = (mem.TotalCommittedBytes - _commitWindow[0].Commit) / 1073741824.0;
        long commitFree = mem.TotalCommitLimitBytes - mem.TotalCommittedBytes;
        double freeGb = mem.AvailablePhysicalBytes / 1073741824.0;
        double commitFreeGb = commitFree / 1073741824.0;
        double limitGb = mem.TotalCommitLimitBytes / 1073741824.0;

        var level = EvaluateCommitWatch(growthGb, freeGb, commitFreeGb, limitGb);
        if (level == CommitWatchLevel.None) return;

        _lastCommitWatchUtc = now;
        bool critical = level == CommitWatchLevel.Critical;
        string reason = critical
            ? $"提交内存余量低于总量 10%（可用 {freeGb:F1} GB，提交余量 {commitFreeGb:F1} GB，仅提示，不自动处置）"
            : $"提交内存 10 分钟内增长 {growthGb:F1} GB 且余量吃紧（可用 {freeGb:F1} GB，提交余量 {commitFreeGb:F1} GB，仅提示，不自动处置）";
        _decisions.Log(new DecisionEntry
        {
            Kind = "CommitWatch",
            Target = "-",
            Decision = "提示",
            Reason = reason
        });
        // 触达层：真正会把人叫醒的通道（决策日志之外的托盘气泡/主页横幅）
        if (_config.Config.ShowAlerts)
        {
            try
            {
                Alert?.Invoke(critical ? "内存告急" : "内存吃紧",
                    critical
                        ? $"提交余量仅 {commitFreeGb:F1} GB（可用 {freeGb:F1} GB）。主页横幅可一键低风险腾内存。"
                        : $"提交内存 10 分钟增长 {growthGb:F1} GB，可用降至 {freeGb:F1} GB。主页横幅可一键低风险腾内存。",
                    critical);
            }
            catch { }
        }
    }

    // ---- habit profile hint (data-driven, advisory only) ----

    private void CheckHabitHint()
    {
        try
        {
            var nowLocal = DateTime.Now;
            _habit ??= HabitProfile.Build(_usage.ReadWindow(TimeSpan.FromDays(14)), nowLocal);
            if (_habit.Hours.Count == 0) return;
            if (nowLocal.Minute < 30) return;   // hint 30 minutes ahead of the tight window

            int nextHour = (nowLocal.Hour + 1) % 24;
            var hp = _habit.ForHour(nextHour);
            if (hp == null || hp.Level is not (PressureLevel.Tight or PressureLevel.Critical)) return;
            // v0.17.0：**全局**每天 1 条。v0.13 的实现是"每时段 24h"，实测 15 条/天
            // （11-10 数据），与"降为每日 1 条"的意图不符。
            if ((nowLocal - _lastHabitHintUtc).TotalHours < 24) return;

            _lastHabitHintHour = nextHour;
            _lastHabitHintUtc = nowLocal;
            _decisions.Log(new DecisionEntry
            {
                Kind = "HabitHint",
                Target = $"{nextHour:00}:00-{(nextHour + 1) % 24:00}:00",
                Decision = "提示",
                Reason = $"历史画像：该时段可用内存常态偏低（P25 {hp.P25AvailGb:F1} GB，{hp.SampleCount} 条样本），可提前腾内存（仅提示，不自动处置）"
            });
            if (_config.Config.ShowAlerts)
            {
                try
                {
                    Alert?.Invoke("时段提醒",
                        $"历史画像：{nextHour:00}-{(nextHour + 1) % 24:00} 点可用内存常态偏低（P25 {hp.P25AvailGb:F1} GB），可提前腾内存。",
                        false);
                }
                catch { }
            }
        }
        catch { }
    }
}
