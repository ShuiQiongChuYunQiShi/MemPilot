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
    private int _intervalSeconds = 30;
    private Func<bool> _enabled = () => true;
    private Func<IReadOnlyList<ProcessInfo>>? _procs;
    private Func<IReadOnlyDictionary<int, double>>? _gpuPercent;

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
        Func<IReadOnlyDictionary<int, double>>? gpuPercent = null)
    {
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
            if (!_enabled()) return;
            var snap = _mem.Sample();
            LastSnapshot = snap;
            var adapters = _info.GetAdapters(snap); // 10s 缓存 + PDH 兜底
            Last = _recorder.Write(snap,
                _procs?.Invoke() ?? Array.Empty<ProcessInfo>(),
                adapters,
                _gpuPercent?.Invoke(),
                _intervalSeconds,
                DateTime.UtcNow);
            LastError = snap.Available ? "" : (_mem.LastDiag.Length > 0 ? _mem.LastDiag : "gpu memory unavailable");
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
