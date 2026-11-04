using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 显存采样器（v0.3.4）：独立定时器，默认 30 秒一次，与 UI 刷新节奏解耦
/// （窗口最小化到托盘时刷新会降到 30 秒+，但显存尖峰不能被这个节奏带着走）。
///
/// 只采样与落盘，不做任何处置与告警——阈值等真实数据攒够再定。
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    public const int MinIntervalSeconds = 15;
    public const int MaxIntervalSeconds = 300;

    private readonly GpuMemoryService _mem = new();
    private readonly GpuInfoService _info = new();
    private readonly GpuRecorder _recorder;

    private Timer? _timer;
    private int _ticking;
    private bool _reportedUnavailable;
    private readonly Dictionary<int, string> _pidNames = new();

    /// <summary>AI 会话检测（v0.5.0）：N-of-M + 滞回，只观测不处置。</summary>
    public AiSessionDetector Session { get; } = new();
    public bool AiSessionActive => Session.Active;
    /// <summary>会话开始/结束（summary, started）。30 秒采样驱动，池线程触发。</summary>
    public event Action<AiSessionSummary, bool>? AiSessionChanged;
    private int _intervalSeconds = 30;
    private Func<bool> _enabled = () => true;
    private Func<IReadOnlyList<ProcessInfo>>? _procs;
    private Func<IReadOnlyDictionary<int, double>>? _gpuPercent;
    private Func<double?>? _commitFreePct;

    /// <summary>最近一条样本（主页/诊断可读）。</summary>
    public GpuSample? Last { get; private set; }
    /// <summary>最近一次显存快照（含逐进程明细，VM 用它填进程页列）。</summary>
    public GpuMemorySnapshot? LastSnapshot { get; private set; }
    /// <summary>最近一次失败原因（成功时为空）。</summary>
    public string LastError { get; private set; } = "";

    public GpuMonitor(string dataDir, int retentionDays = 30)
    {
        _recorder = new GpuRecorder(dataDir, retentionDays);
    }

    public void Start(int intervalSeconds, Func<bool> enabled,
        Func<IReadOnlyList<ProcessInfo>>? procs = null,
        Func<IReadOnlyDictionary<int, double>>? gpuPercent = null,
        Func<double?>? commitFreePct = null)
    {
        _commitFreePct = commitFreePct;
        _intervalSeconds = Math.Clamp(intervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        _enabled = enabled ?? (() => true);
        _procs = procs;
        _gpuPercent = gpuPercent;
        Stop();
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(_intervalSeconds));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (!_enabled())
            {
                // v0.9.0：采样被关掉时若会话仍"进行中"，会永久置 AiSessionActive=true，
                // 而 CommitReliefPolicy 见到它就一律不动作 → 提交兜底静默失效直到重启。
                if (Session.Active && Session.Current is { } open)
                {
                    open.EndUtc = DateTime.UtcNow;
                    AiSessionChanged?.Invoke(open, false);
                }
                return;
            }
            var snap = _mem.Sample();
            LastSnapshot = snap;
            var procs = _procs?.Invoke() ?? Array.Empty<ProcessInfo>();
            foreach (var p in procs) _pidNames[p.Pid] = p.Name;      // 名称缓存：进程快照缺项时兜底
            if (_pidNames.Count > 4096) _pidNames.Clear();
            var adapters = _info.GetAdapters(snap); // 10s 缓存 + PDH 兜底
            Last = _recorder.Write(snap,
                procs,
                adapters,
                _gpuPercent?.Invoke(),
                _intervalSeconds,
                DateTime.UtcNow,
                pid => _pidNames.TryGetValue(pid, out var n) ? n : null);

            // AI 会话检测（用刚写出的样本，保证与会话历史一致）
            var update = Session.Push(Last.Used, Last.Total, Last.T, Last.Top, _commitFreePct?.Invoke());
            if (update.Event != AiSessionEvent.None)
                AiSessionChanged?.Invoke(update.Summary!, update.Event == AiSessionEvent.Started);
            LastError = snap.Available ? "" : (_mem.LastDiag.Length > 0 ? _mem.LastDiag : "gpu memory unavailable");
            if (!snap.Available && !_reportedUnavailable)
            {
                _reportedUnavailable = true;                       // 只记一次，可用后复位
                Helpers.Diag.Record("gpu-mem", LastError);
            }
            else if (snap.Available) _reportedUnavailable = false;
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    public List<GpuSample> ReadWindow(TimeSpan window) => _recorder.ReadWindow(window);

    public void Dispose()
    {
        Stop();
        _mem.Dispose();
    }
}
