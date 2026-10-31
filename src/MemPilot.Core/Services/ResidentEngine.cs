using System.Diagnostics;
using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

public class ModeSwitchEventArgs : EventArgs
{
    public AppMode Mode { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// The resident engine: a single background tick that triggers refreshes and applies
/// mode-driven policies. Replaces the three ad-hoc UI timers:
///   - periodic refresh (so idle detection / CPU rates actually progress unattended);
///   - signal-based mode switching with hysteresis (fullscreen D3D + anticheat presence → Game,
///     AI tool present with real memory footprint → AiGeneration, dev foreground → Development);
///   - per-process auto cleanup capped at L1/L3 (L4 is never automatic);
///   - system-list auto optimization, now pressure/interval gated and skipped in fullscreen.
/// Every switch/action/skip is written to the DecisionLog so automatic behavior is explainable.
/// </summary>
public class ResidentEngine
{
    private readonly ConfigStore _config;
    private readonly CleanupEngine _cleanup;
    private readonly NativeMemoryCleanerService _memCleaner;
    private readonly AuditLogger _audit;
    private readonly DecisionLog _decisions;
    private readonly UsageRecorder _usage;
    private readonly BehaviorLedger? _behaviors;
    private readonly GpuUsageService _gpuUsage = new();
    private readonly List<(DateTime utc, double idle)> _inputSamples = new();
    private AppMode? _lastShadowTarget;
    private DateTime _lastShadowLogUtc = DateTime.MinValue;
    /// <summary>最近一次场景判定（诊断面板用）。</summary>
    public SceneDecision? LastSceneDecision { get; private set; }
    /// <summary>最近一次逐进程 GPU%（v0.3.4，显存采样器复用；不可用时为空字典）。</summary>
    public IReadOnlyDictionary<int, double> LastGpuPercent { get; private set; } = new Dictionary<int, double>();
    private int _intervalSeconds;

    private System.Threading.Timer? _timer;
    private readonly object _tickLock = new();
    private bool _ticking;
    private int _modeStreak;
    private AppMode? _pendingMode;
    private DateTime _lastModeSwitchUtc = DateTime.MinValue;
    private DateTime _lastAutoOptimizeUtc = DateTime.MinValue;
    private readonly Dictionary<int, DateTime> _lastAutoActionUtc = new();
    private readonly Dictionary<int, DateTime> _lastSkipLogUtc = new();
    // keyed by (pid, start time): PID reuse must not suppress a new process's first alert
    private readonly Dictionary<(int pid, DateTime start), DateTime> _lastGrowthLogUtc = new();
    private readonly Dictionary<(int pid, DateTime start), DateTime> _lastJunkSweepUtc = new();
    private readonly List<(DateTime Utc, long Commit, long Avail, long Limit)> _commitWindow = new();
    private DateTime _lastCommitWatchUtc = DateTime.MinValue;
    private HabitProfile? _habit;
    private DateTime _lastHabitHintUtc = DateTime.MinValue;
    private int _lastHabitHintHour = -1;
    private int _lastForegroundPid;

    public int LastForegroundPid => _lastForegroundPid;
    public bool ForegroundFullscreen { get; private set; }
    public string ForegroundProcessName { get; private set; } = "";

    /// <summary>重要提示触达（托盘气泡/主页横幅）。参数：标题、内容、是否警告级。</summary>
    public event Action<string, string, bool>? Alert;

    public ResidentEngine(ConfigStore config, CleanupEngine cleanup, NativeMemoryCleanerService memCleaner,
        AuditLogger audit, DecisionLog decisions, UsageRecorder usage, BehaviorLedger? behaviors = null)
    {
        _config = config;
        _cleanup = cleanup;
        _memCleaner = memCleaner;
        _audit = audit;
        _decisions = decisions;
        _usage = usage;
        _behaviors = behaviors;
        _intervalSeconds = Math.Max(3, config.Config.RefreshIntervalSeconds);
    }

    /// <summary>Start ticking. refreshAsync runs on a background thread (VM marshals UI updates).</summary>
    public void Start(Func<CancellationToken, Task> refreshAsync)
    {
        Stop();
        var interval = TimeSpan.FromSeconds(Math.Max(3, _config.Config.RefreshIntervalSeconds));
        _timer = new System.Threading.Timer(async _ =>
        {
            bool taken = false;
            try
            {
                lock (_tickLock)
                {
                    if (_ticking) return;   // never stack ticks
                    _ticking = true;
                    taken = true;
                }
                await refreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            finally { if (taken) { lock (_tickLock) { _ticking = false; } } }
        }, null, interval, interval);
    }

    public void Stop() => _timer?.Dispose();

    /// <summary>Retune the tick period at runtime (tray lightweight mode uses a slower cadence).</summary>
    public void SetIntervalSeconds(int seconds)
    {
        _intervalSeconds = Math.Max(3, seconds);
        var interval = TimeSpan.FromSeconds(_intervalSeconds);
        _timer?.Change(interval, interval);
    }

    /// <summary>Called by the VM after every completed refresh, with the fresh snapshot.</summary>
    public void AfterRefresh(List<ProcessInfo> procs, SystemMemoryInfo mem)
    {
        var cfg = _config.Config;
        SampleForeground();
        EvaluateScene(procs);
        if (cfg.AutoCleanupEnabled && cfg.CurrentMode is AppMode.Development or AppMode.Game or AppMode.AiGeneration)
            AutoCleanup(procs);
        if (cfg.AutoOptimizeEnabled) AutoOptimize(mem);
        WatchMemoryGrowth(procs);
        WatchCommitPressure(mem);
        CheckHabitHint();
        _behaviors?.Observe(procs);
        if (cfg.AutoJunkSweepEnabled) AutoJunkSweep(procs);
        _usage.RecordSample(mem, procs, cfg.CurrentMode, _intervalSeconds);
    }

    // ---- foreground / fullscreen sampling ----

    public void SampleForeground()
    {
        try
        {
            ForegroundFullscreen = IsForegroundFullscreen();
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { _lastForegroundPid = 0; ForegroundProcessName = ""; return; }
            GetWindowThreadProcessId(hwnd, out uint pid);
            _lastForegroundPid = (int)pid;
            try { ForegroundProcessName = Process.GetProcessById((int)pid).ProcessName; }
            catch { ForegroundProcessName = ""; }
        }
        catch { }
    }

    private static bool IsForegroundFullscreen()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
                state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
                return true;
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;
            var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;
            return rect.Left <= mi.rcMonitor.Left && rect.Top <= mi.rcMonitor.Top &&
                   rect.Right >= mi.rcMonitor.Right && rect.Bottom >= mi.rcMonitor.Bottom;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- mode switching with hysteresis ----

    // ---- 场景识别 v2（v0.2.2）：多信号打分 + 影子模式 + 自动切换 ----

    private void EvaluateScene(List<ProcessInfo> procs)
    {
        try
        {
            var gpuMap = _gpuUsage.Sample();
            LastGpuPercent = gpuMap; // v0.3.4：供显存采样器记录逐进程 GPU%（复用同一次 PDH 采集）
            var now = DateTime.UtcNow;
            double idle = InputActivity.IdleSeconds();
            _inputSamples.Add((now, idle));
            _inputSamples.RemoveAll(x => (now - x.utc).TotalSeconds > 120);
            double inputRatio = _inputSamples.Count == 0 ? 0
                : (double)_inputSamples.Count(x => x.idle < 5) / _inputSamples.Count;

            var fg = procs.FirstOrDefault(p => p.Pid == _lastForegroundPid);
            double FgGpu(int pid) => gpuMap.TryGetValue(pid, out var g) ? g : -1;
            double aiGpu = procs.Where(p => p.Category == ProcessCategory.AiImageVideo)
                                .Select(p => FgGpu(p.Pid)).DefaultIfEmpty(-1).Max();
            double aiCpu = procs.Where(p => p.Category == ProcessCategory.AiImageVideo)
                                .Sum(p => p.Memory.CpuPercent);

            var signals = new SceneSignals
            {
                ForegroundFullscreen = ForegroundFullscreen,
                ForegroundName = fg?.Name ?? ForegroundProcessName,
                ForegroundTitle = fg?.MainWindowTitle ?? "",
                ForegroundCategory = fg?.Category ?? ProcessCategory.Unknown,
                ForegroundGpu = fg != null ? FgGpu(fg.Pid) : -1,
                AnyAntiCheat = procs.Any(p => p.Category == ProcessCategory.AntiCheat),
                GameProcessBusy = procs.Any(p => p.Category == ProcessCategory.Game &&
                    (p.Memory.CpuPercent > 5 || FgGpu(p.Pid) > 10)),
                AiProcessResident = procs.Any(p => p.Category == ProcessCategory.AiImageVideo &&
                    p.Memory.PrivateBytes > 512L * 1024 * 1024),
                AiGpu = aiGpu,
                AiBusy = aiCpu > 30 || aiGpu > 20,
                AudioActive = procs.Any(p => p.AudioActive),
                InputIdleSeconds = idle,
                RecentInputRatio = inputRatio,
                DevForeground = fg?.Category == ProcessCategory.DevTool,
                DevCompileActive = procs.Any(p => p.Category == ProcessCategory.DevTool &&
                    (p.Memory.CpuPercent > 10 || FgGpu(p.Pid) > 5)),
            };

            var decision = SceneDetector.Evaluate(signals);
            LastSceneDecision = decision;

            var cfg = _config.Config;
            if (cfg.AutoModeSwitch)
            {
                ApplySceneDecision(decision);
            }
            else if (cfg.SceneShadowEnabled && decision.Decisive && decision.Target.HasValue)
            {
                // 影子模式：只记录"如果自动切换，会切到哪"，绝不动作；目标变化且距上次 ≥5 分钟才记
                if (decision.Target != _lastShadowTarget && (now - _lastShadowLogUtc).TotalSeconds >= 300)
                {
                    _lastShadowTarget = decision.Target;
                    _lastShadowLogUtc = now;
                    _decisions.Log(new DecisionEntry
                    {
                        Kind = "SceneShadow",
                        Target = decision.Target.Value.ToString(),
                        Decision = "如果自动切换：会切",
                        Reason = decision.Reason + "；信号：" + string.Join("、", decision.Signals.Take(6))
                    });
                }
            }
        }
        catch { }
    }

    private void ApplySceneDecision(SceneDecision decision)
    {
        var cfg = _config.Config;
        if (!decision.Decisive || !decision.Target.HasValue)
        {
            _pendingMode = null;
            _modeStreak = 0;
            return;
        }
        var target = decision.Target.Value;
        if (target == cfg.CurrentMode)
        {
            _pendingMode = null;
            _modeStreak = 0;
            return;
        }
        if (_pendingMode == target) _modeStreak++;
        else { _pendingMode = target; _modeStreak = 1; }
        if (_modeStreak < 2 || (DateTime.UtcNow - _lastModeSwitchUtc).TotalSeconds < 60) return;

        var from = cfg.CurrentMode;
        cfg.CurrentMode = target;
        _config.Save();
        _lastModeSwitchUtc = DateTime.UtcNow;
        _pendingMode = null;
        _modeStreak = 0;
        _decisions.Log(new DecisionEntry
        {
            Kind = "ModeSwitch",
            Target = $"{from} → {target}",
            Decision = "自动切换",
            Reason = $"{decision.Reason}（滞回确认 2 轮）"
        });
        MaybeResumeSuspendedOnModeChange();
        ModeChanged?.Invoke(this, new ModeSwitchEventArgs { Mode = target, Reason = decision.Reason });
    }

    public event EventHandler<ModeSwitchEventArgs>? ModeChanged;

    private void MaybeResumeSuspendedOnModeChange()
    {
        if (!_config.Config.ResumeSuspendedOnModeChange) return;
        var records = _cleanup.Suspended.LiveRecords();
        foreach (var rec in records)
            _cleanup.Resume(rec.Pid, rec.Name);
        if (records.Count > 0)
            _decisions.Log(new DecisionEntry
            {
                Kind = "AutoCleanup",
                Target = $"{records.Count} 个挂起进程",
                Decision = "恢复",
                Reason = "模式切换且开启了“切模式自动恢复挂起”"
            });
    }

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

    private void AutoOptimize(SystemMemoryInfo mem)
    {
        var cfg = _config.Config;
        double freePct = mem.TotalPhysicalBytes <= 0 ? 100 : (double)mem.AvailablePhysicalBytes / mem.TotalPhysicalBytes * 100;
        bool lowMemory = cfg.AutoOptimizeOnLowMemory && freePct < cfg.AutoOptimizeThresholdPercent;
        bool intervalDue = cfg.AutoOptimizeIntervalEnabled &&
                           (DateTime.UtcNow - _lastAutoOptimizeUtc).TotalMinutes >= cfg.AutoOptimizeIntervalMinutes;

        if (cfg.SkipOptimizeInFullscreen && ForegroundFullscreen)
        {
            if (lowMemory || intervalDue)
                _decisions.Log(new DecisionEntry
                {
                    Kind = "AutoOptimize",
                    Target = "-",
                    Decision = "跳过",
                    Reason = $"前台全屏（游戏）时禁用系统级清理（可用 {freePct:F0}%）"
                });
            return;
        }
        if (!lowMemory && !intervalDue) return;

        _lastAutoOptimizeUtc = DateTime.UtcNow;
        // low-priority standby purge only: full standby purge / system file cache trimming
        // actively hurt running games (texture caches live in standby)
        _memCleaner.CleanAreas(new List<MemoryArea> { MemoryArea.StandbyListLowPriority });
        var trigger = lowMemory ? $"低内存触发（可用 {freePct:F0}%）" : "周期触发";
        _audit.Log(new AuditEntry
        {
            Action = "AutoOptimize",
            Target = "StandbyListLowPriority",
            Level = "Auto",
            Result = trigger,
            MemoryBeforeBytes = mem.AvailablePhysicalBytes,
            MemoryAfterBytes = mem.AvailablePhysicalBytes
        });
        _decisions.Log(new DecisionEntry
        {
            Kind = "AutoOptimize",
            Target = "StandbyListLowPriority",
            Decision = "执行",
            Reason = trigger
        });
    }

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
