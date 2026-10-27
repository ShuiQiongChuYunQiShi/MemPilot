using System.Diagnostics;
using System.Runtime.InteropServices;
using MemoryTool.Helpers;
using MemoryTool.Models;

namespace MemoryTool.Services;

/// <summary>
/// The resident engine: a single background tick that triggers refreshes and applies
/// mode-driven policies. Replaces the three ad-hoc UI timers:
///   - periodic refresh (so idle detection / CPU rates actually progress unattended);
///   - signal-based mode switching with hysteresis (fullscreen D3D + anticheat presence → Game,
///     AI tool present with real memory footprint → AiGeneration, dev foreground → Development);
///   - per-process auto cleanup capped at L1/L3 (L4 is never automatic);
///   - system-list auto optimization, now pressure/interval gated and skipped in fullscreen.
/// </summary>
public class ResidentEngine
{
    private readonly ConfigStore _config;
    private readonly CleanupEngine _cleanup;
    private readonly NativeMemoryCleanerService _memCleaner;
    private readonly AuditLogger _audit;

    private System.Threading.Timer? _timer;
    private readonly object _tickLock = new();
    private bool _ticking;
    private int _modeStreak;
    private AppMode? _pendingMode;
    private DateTime _lastModeSwitchUtc = DateTime.MinValue;
    private DateTime _lastAutoOptimizeUtc = DateTime.MinValue;
    private readonly Dictionary<int, DateTime> _lastAutoActionUtc = new();
    private int _lastForegroundPid;

    public int LastForegroundPid => _lastForegroundPid;
    public bool ForegroundFullscreen { get; private set; }
    public string ForegroundProcessName { get; private set; } = "";

    public ResidentEngine(ConfigStore config, CleanupEngine cleanup, NativeMemoryCleanerService memCleaner, AuditLogger audit)
    {
        _config = config;
        _cleanup = cleanup;
        _memCleaner = memCleaner;
        _audit = audit;
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

    /// <summary>Called by the VM after every completed refresh, with the fresh snapshot.</summary>
    public void AfterRefresh(List<ProcessInfo> procs, SystemMemoryInfo mem)
    {
        var cfg = _config.Config;
        SampleForeground();
        if (cfg.AutoModeSwitch) AutoSwitchMode(procs);
        if (cfg.AutoCleanupEnabled && cfg.CurrentMode is AppMode.Development or AppMode.Game or AppMode.AiGeneration)
            AutoCleanup(procs);
        if (cfg.AutoOptimizeEnabled) AutoOptimize(mem);
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

    private void AutoSwitchMode(List<ProcessInfo> procs)
    {
        AppMode? target = null;

        // Game signal: D3D fullscreen foreground, or anticheat/known game processes alive
        if (ForegroundFullscreen ||
            procs.Any(p => p.Category == ProcessCategory.AntiCheat ||
                           (p.Category == ProcessCategory.Game && !p.HasVisibleWindow && p.Memory.CpuPercent > 5)))
            target = AppMode.Game;

        // AI signal: AI tool processes actually resident (not just leftover)
        if (target == null &&
            procs.Any(p => p.Category == ProcessCategory.AiImageVideo &&
                           p.Memory.PrivateBytes > 512L * 1024 * 1024))
            target = AppMode.AiGeneration;

        // Dev signal: foreground window belongs to a dev tool
        if (target == null)
        {
            var fg = procs.FirstOrDefault(p => p.Pid == _lastForegroundPid);
            if (fg != null && fg.Category == ProcessCategory.DevTool) target = AppMode.Development;
        }

        if (target == null || target.Value == _config.Config.CurrentMode)
        {
            _pendingMode = null;
            _modeStreak = 0;
            return;
        }

        // hysteresis: same target twice in a row, and no switch more often than 60s
        if (_pendingMode == target.Value) _modeStreak++;
        else { _pendingMode = target.Value; _modeStreak = 1; }

        if (_modeStreak >= 2 && (DateTime.UtcNow - _lastModeSwitchUtc).TotalSeconds >= 60)
        {
            _config.Config.CurrentMode = target.Value;
            _config.Save();
            _lastModeSwitchUtc = DateTime.UtcNow;
            _pendingMode = null;
            _modeStreak = 0;
            ModeChanged?.Invoke(this, target.Value);
        }
    }

    public event EventHandler<AppMode>? ModeChanged;

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
            if (p.IsProtected || p.IsSystemProcess || p.IsService || p.IsWhitelisted || p.ModeKeep) continue;
            if (p.Category is ProcessCategory.Unknown or ProcessCategory.SystemCritical
                or ProcessCategory.SecuritySoftware or ProcessCategory.AntiCheat or ProcessCategory.Driver) continue;
            if (p.Pid == _lastForegroundPid || p.AudioActive) continue;
            if (p.StartTime != DateTime.MinValue && (DateTime.Now - p.StartTime).TotalSeconds < cfg.MinProcessAgeSeconds) continue;
            // per-process cooldown: at most one auto action per 10 minutes
            if (_lastAutoActionUtc.TryGetValue(p.Pid, out var last) && (now - last).TotalMinutes < 10) continue;

            bool matches = profile.CleanPatterns.Any(pat =>
                p.Name.Contains(pat, StringComparison.OrdinalIgnoreCase) ||
                p.ExecutablePath.Contains(pat, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

            if (cfg.AutoCloseCleanMatches && p.HasVisibleWindow)
            {
                _cleanup.Execute(p, CleanupActionLevel.L3GracefulClose, cfg.GracefulCloseTimeoutSeconds, escalateToKill: false);
                _lastAutoActionUtc[p.Pid] = now;
            }
            else
            {
                _cleanup.Execute(p, CleanupActionLevel.L1Trim, 0);
                _lastAutoActionUtc[p.Pid] = now;
            }
        }
        if (_lastAutoActionUtc.Count > 2048) _lastAutoActionUtc.Clear();
    }

    // ---- system-list auto optimization, pressure/interval gated, fullscreen-aware ----

    private void AutoOptimize(SystemMemoryInfo mem)
    {
        var cfg = _config.Config;
        if (cfg.SkipOptimizeInFullscreen && ForegroundFullscreen) return;

        double freePct = mem.TotalPhysicalBytes <= 0 ? 100 : (double)mem.AvailablePhysicalBytes / mem.TotalPhysicalBytes * 100;
        bool lowMemory = cfg.AutoOptimizeOnLowMemory && freePct < cfg.AutoOptimizeThresholdPercent;
        bool intervalDue = cfg.AutoOptimizeIntervalEnabled &&
                           (DateTime.UtcNow - _lastAutoOptimizeUtc).TotalMinutes >= cfg.AutoOptimizeIntervalMinutes;
        if (!lowMemory && !intervalDue) return;

        _lastAutoOptimizeUtc = DateTime.UtcNow;
        // low-priority standby purge only: full standby purge / system file cache trimming
        // actively hurt running games (texture caches live in standby)
        _memCleaner.CleanAreas(new List<MemoryArea> { MemoryArea.StandbyListLowPriority });
        _audit.Log(new AuditEntry
        {
            Action = "AutoOptimize",
            Target = "StandbyListLowPriority",
            Level = "Auto",
            Result = lowMemory ? $"低内存触发（可用 {freePct:F0}%）" : "周期触发",
            MemoryBeforeBytes = mem.AvailablePhysicalBytes,
            MemoryAfterBytes = mem.AvailablePhysicalBytes
        });
    }
}
