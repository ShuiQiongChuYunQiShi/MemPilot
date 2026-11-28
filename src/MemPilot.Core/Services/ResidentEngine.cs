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

    /// <summary>
    /// Start ticking. refreshAsync runs on a background thread (VM marshals UI updates).
    ///
    /// v0.18.0：双频调度。定时器固定 5s 跑一次**轻探针**（只读系统内存 + 处理到期挂起恢复，
    /// 都是微秒级、不枚举进程）；是否升级为全量刷新由 <see cref="ShouldFullRefresh"/> 裁决——
    /// 压力立即提频、UI 可见按配置间隔、托盘且无压力降到 max(60, 3×间隔)。
    /// 旧实现无论有没有压力都每 10 秒枚举一次全部进程，而实测 89% 的样本没有压力。
    /// </summary>
    public void Start(Func<CancellationToken, Task> refreshAsync)
    {
        Stop();
        ResumeExpiredSuspends();   // v0.15.0：应用关闭期间到期的定时挂起，启动即恢复
        _sinceFullRefresh.Restart();
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

                // ---- 轻探针：不依赖进程快照 ----
                RunStage("probe-resume", ResumeExpiredSuspends);
                SelfCost?.Heartbeat(StageFailures);

                SystemMemoryInfo mem;
                try { mem = _probe.GetSystemMemory(); }
                catch (Exception ex) { Helpers.Diag.Record("tick:probe-mem", ex); return; }
                bool pressure = IsPressure(mem);

                if (!ShouldFullRefresh(_sinceFullRefresh.Elapsed, UiActive, pressure, _intervalSeconds)) return;
                _sinceFullRefresh.Restart();
                var sw = Stopwatch.StartNew();
                try { await refreshAsync(CancellationToken.None).ConfigureAwait(false); }
                finally
                {
                    // v0.25.0：分段快照过期（本次刷新在写段之前就抛了）时用 Zero，
                    // 否则会把上一轮的分段配到本轮的慢总耗时上，归因指向错误阶段。
                    var timings = (DateTime.UtcNow - LastTimingsUtc).TotalSeconds < 30
                        ? LastTimings
                        : RefreshTimings.Zero;
                    SelfCost?.RecordTick(sw.ElapsedMilliseconds, timings, StageFailures);
                }
            }
            catch { }
            finally { if (taken) { lock (_tickLock) { _ticking = false; } } }
        }, null, LightProbeIntervalSeconds, LightProbeIntervalSeconds);
    }

    /// <summary>轻探针周期：固定 5s，与用户配置的全量间隔解耦。</summary>
    public const int LightProbeIntervalSeconds = 5;

    private readonly Stopwatch _sinceFullRefresh = new();

    /// <summary>UI 是否可见/活跃（壳层设置）。不可见时全量刷新降频，但压力探测仍每 5s 一次。</summary>
    public bool UiActive { get; set; } = true;

    /// <summary>自身开销记录（v0.18.0，由 VM 注入）。</summary>
    public SelfCostRecorder? SelfCost { get; set; }

    /// <summary>
    /// 最近一次全量刷新的分段耗时（v0.20.0，由 VM 的 RefreshAsync 写入）。
    /// 引用类型单次赋值天然原子；tick 有防重入，读到的就是本次刷新的值。
    /// 首帧/手动刷新前为全 0。
    /// </summary>
    public RefreshTimings LastTimings { get; set; } = RefreshTimings.Zero;

    /// <summary>LastTimings 的写入时刻（v0.25.0）。用于识别"本次刷新异常、分段快照是上一轮的"
    /// ——那种情况下总耗时与分段来自不同 tick，归因会指向错误的阶段。</summary>
    public DateTime LastTimingsUtc { get; set; }

    /// <summary>压力判定（与 WatchAvailableFloor 同阈值）：可用 &lt;3 GB 或提交余量 &lt;10%。</summary>
    public static bool IsPressure(SystemMemoryInfo mem)
    {
        double availGb = mem.AvailablePhysicalBytes / 1073741824.0;
        double limitGb = mem.TotalCommitLimitBytes / 1073741824.0;
        double commitFreeGb = (mem.TotalCommitLimitBytes - mem.TotalCommittedBytes) / 1073741824.0;
        return availGb < 3.0 || (limitGb > 0 && commitFreeGb / limitGb * 100 < 10);
    }

    /// <summary>
    /// 全量刷新频率裁决（纯函数，可单测）：
    /// 压力优先（最快 5s，与轻探针同频）；UI 可见按配置间隔；不可见且无压力按 max(60, 3×间隔)。
    /// </summary>
    public static bool ShouldFullRefresh(TimeSpan sinceLast, bool uiActive, bool pressure, int intervalSeconds)
    {
        int fullInterval = Math.Max(3, intervalSeconds);
        if (pressure) return sinceLast.TotalSeconds >= Math.Min(5, fullInterval);
        if (uiActive) return sinceLast.TotalSeconds >= fullInterval;
        return sinceLast.TotalSeconds >= Math.Max(60, fullInterval * 3);
    }

    public void Stop() => _timer?.Dispose();

    /// <summary>Retune the full-refresh interval at runtime.
    /// v0.18.0：定时器固定 5s（轻探针），这里只改"全量刷新"的目标间隔，不再 Change 定时器周期。</summary>
    public void SetIntervalSeconds(int seconds)
    {
        _intervalSeconds = Math.Max(3, seconds);
    }

    /// <summary>垃圾进程建议（v0.15.0）：一天最多触发一次，VM 负责弹卡 + 气球。</summary>
    public event Action<JunkAdvice>? JunkAdvised;
    /// <summary>WSL 常驻提示（v0.22.0）：vmmemWSL 占用够大且今天零活动，只提示不执行。</summary>
    public event Action<WslHint>? WslHintAdvised;
    /// <summary>用户标记为「可清理」的进程名提供者（v0.15.1，由 VM 注入 FeedbackStore）。</summary>
    public Func<IReadOnlyCollection<string>>? MarkedCleanableProvider { get; set; }
    private DateTime _lastJunkAdviceDay = DateTime.MinValue.Date;
    /// <summary>建议学习账本（v0.15.0，只调频率不加白名单）。</summary>
    public JunkAdviceStore? JunkAdvice { get; set; }

    /// <summary>Called by the VM after every completed refresh, with the fresh snapshot.</summary>
    public void AfterRefresh(List<ProcessInfo> procs, SystemMemoryInfo mem)
    {
        var cfg = _config.Config;
        _afterStageMs = 0;
        // v0.18.0（P0）：逐阶段隔离。旧实现是一个 try 包住整条流水线，任一阶段抛出都会
        // 终止同 tick 的后续阶段——而"到期恢复挂起"排在队尾，前面的提交兜底一抛异常，
        // 已挂起的进程就不会被恢复。现在每阶段独立 try（RunStage），失败记 Diag 并累计。
        RunStage("foreground", SampleForeground);
        RunStage("scene", () => EvaluateScene(procs));
        if (cfg.AutoCleanupEnabled && cfg.CurrentMode is AppMode.Development or AppMode.Game or AppMode.AiGeneration)
            RunStage("autoclean", () => AutoCleanup(procs));
        RunStage("growth", () => WatchMemoryGrowth(procs));
        RunStage("commit-pressure", () => WatchCommitPressure(mem));
        RunStage("commit-relief", () => WatchCommitRelief(procs, mem));
        RunStage("available-floor", () => WatchAvailableFloor(procs, mem));
        RunStage("habit-hint", CheckHabitHint);
        RunStage("behaviors", () => _behaviors?.Observe(procs));
        RunStage("junk-advice", () => MaybeAdviseJunk(procs));
        RunStage("long-resident-advice", () => MaybeAdviseLongResident(procs));   // v0.21.0
        RunStage("wsl-hint", () => MaybeAdviseWsl(procs));                          // v0.22.0
        RunStage("junk-resume", ResumeExpiredSuspends);   // P2 起改由 5s 轻探针驱动，这里保留兜底
        // v0.26.0：结算 60 秒前压缩的进程"回弹了多少"——这是 trim 排序的真实指标
        RunStage("trim-feedback", () => _cleanup.TrimFeedback.SettleDue(
            procs, DateTime.UtcNow, pid => _probe.ReadPrivateWorkingSet(pid)));
        if (cfg.AutoJunkSweepEnabled) RunStage("junk-sweep", () => AutoJunkSweep(procs));
        RunStage("usage", () => _usage.RecordSample(mem, procs, cfg.CurrentMode, _intervalSeconds));

        // v0.25.0：把整条阶段流水线的耗时并回分段快照。此前 available-floor（低可用压缩，
        // 单次可达 0.7s）完全不在任何分段里，慢 tick 无法归因。
        LastTimings = LastTimings with { AfterMs = _afterStageMs };
    }

    private int _consecutiveStageFailures;
    private int _totalStageFailures;
    /// <summary>本轮 AfterRefresh 各阶段累计耗时（v0.25.0，写进 RefreshTimings.AfterMs）。</summary>
    private long _afterStageMs;
    private DateTime _lastHealthAlertUtc = DateTime.MinValue;

    /// <summary>累计阶段失败次数（诊断/健康度展示用）。</summary>
    public int StageFailures => _totalStageFailures;

    /// <summary>
    /// 单阶段异常隔离：失败只影响自己，不拖垮同 tick 的后续阶段。
    /// 连续 3 次失败 → 托盘/主页告警（30 分钟最多一次），避免"策略静默停摆而界面正常"。
    /// </summary>
    private void RunStage(string name, Action stage)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            stage();
            _consecutiveStageFailures = 0;
        }
        catch (Exception ex)
        {
            Helpers.Diag.Record("tick:" + name, ex);
            _totalStageFailures++;
            if (++_consecutiveStageFailures < 3) return;
            _consecutiveStageFailures = 0;   // 告警后重新计数，避免每 tick 刷屏
            if ((DateTime.UtcNow - _lastHealthAlertUtc).TotalMinutes < 30) return;
            _lastHealthAlertUtc = DateTime.UtcNow;
            try
            {
                Alert?.Invoke("自动策略运行异常",
                    $"连续 3 次有阶段失败（{name}：{ex.Message}）。{Helpers.Diag.Summary()}",
                    true);
            }
            catch { }
        }
        finally { _afterStageMs += sw.ElapsedMilliseconds; }
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
