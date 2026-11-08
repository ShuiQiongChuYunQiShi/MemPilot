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

    private readonly string _sessionFile;

    public GpuMonitor(string dataDir, int retentionDays = 30)
    {
        _recorder = new GpuRecorder(dataDir, retentionDays);
        _sessionFile = System.IO.Path.Combine(dataDir, "ai-session.json");
        TryRestoreSession();
    }

    // ---- AI 会话状态持久化（v0.13.0，四轮复盘）----
    // 实测 11 个"开始"无"结束"全是重启 artifact。会话开始时落盘、结束时删除、
    // 启动时恢复——重启不再把一个 312 分钟的会话劈成两段。

    private void TryRestoreSession()
    {
        try
        {
            if (!System.IO.File.Exists(_sessionFile)) return;
            var summary = System.Text.Json.JsonSerializer.Deserialize<AiSessionSummary>(
                System.IO.File.ReadAllText(_sessionFile));
            if (summary != null && Session.TryRestore(summary))
                LastError = ""; // 恢复成功不打扰用户；决策记录由 VM 在下一次结束时给出完整时长
        }
        catch (Exception ex)
        {
            try { Helpers.Diag.Record("ai-session-restore", ex); } catch { }
        }
    }

    private void SaveSessionState(AiSessionSummary? summary)
    {
        try
        {
            if (summary == null)
            {
                if (System.IO.File.Exists(_sessionFile)) System.IO.File.Delete(_sessionFile);
                return;
            }
            System.IO.File.WriteAllText(_sessionFile,
                System.Text.Json.JsonSerializer.Serialize(summary));
        }
        catch (Exception ex)
        {
            try { Helpers.Diag.Record("ai-session-save", ex); } catch { }
        }
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
                    SaveSessionState(null);   // v0.13.0：结束时同步清掉持久化文件
                    AiSessionChanged?.Invoke(open, false);
                }
                return;
            }
            var snap = _mem.Sample();
            LastSnapshot = snap;
            var procs = _procs?.Invoke() ?? Array.Empty<ProcessInfo>();
            foreach (var p in procs) _pidNames[p.Pid] = p.Name;      // 名称缓存：进程快照缺项时兜底
            // v0.14：按存活集合裁剪（旧实现只在 >4096 时整体清空，死 PID 条目常驻）
            if (_pidNames.Count > 512)
            {
                var alive = new System.Collections.Generic.HashSet<int>(procs.Select(x => x.Pid));
                var dead = _pidNames.Keys.Where(k => !alive.Contains(k)).ToList();
                foreach (var k in dead) _pidNames.Remove(k);
            }
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
            if (update.Event == AiSessionEvent.Started)
            {
                SaveSessionState(update.Summary);
                AiSessionChanged?.Invoke(update.Summary!, true);
            }
            else if (update.Event == AiSessionEvent.Ended)
            {
                SaveSessionState(null);
                AiSessionChanged?.Invoke(update.Summary!, false);
            }
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
