using System.Diagnostics;
using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>压力观测与提醒：内存增长、提交压力（CommitWatch）、时段画像提醒。</summary>
public partial class ResidentEngine
{

    // ---- memory growth watch (leak-ish behavior lands in the decision log, never in actions) ----

    private void WatchMemoryGrowth(List<ProcessInfo> procs)
    {
        const long ThresholdBytes = 512L * 1024 * 1024;
        var now = DateTime.UtcNow;
        foreach (var p in procs)
        {
            if (p.MemoryGrowthBytes < ThresholdBytes) continue;
            if (_lastGrowthLogUtc.TryGetValue(p.StateKey, out var last) && (now - last).TotalMinutes < 30) continue;
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
                        ? $"提交余量仅 {commitFreeGb:F1} GB（可用 {freeGb:F1} GB）。主页可一键低风险腾挪。"
                        : $"提交内存 10 分钟增长 {growthGb:F1} GB，可用降至 {freeGb:F1} GB。主页可一键低风险腾挪。",
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
            if (_lastHabitHintHour == nextHour && (nowLocal - _lastHabitHintUtc).TotalHours < 6) return;

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
