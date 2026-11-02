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
public partial class ResidentEngine
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
        WatchCommitRelief(procs, mem);   // v0.5.0：提交余量兜底（分级 + 安全子集）
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
}
