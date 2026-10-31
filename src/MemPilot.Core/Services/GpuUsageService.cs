using MemPilot.Helpers;

namespace MemPilot.Services;

/// <summary>
/// 逐进程 GPU 使用率（PDH \GPU Engine(*)）——区分"游戏/视频渲染/3D 工作"与普通应用的核心信号。
/// 尽力而为：PDH 不可用（无 GPU/计数器被禁用）时 Available=false，判定层必须把 GPU 当"不可用"
/// 处理（重分配权重），绝不当 0 分。
///
/// v0.3.4：P/Invoke 收敛到 <see cref="PdhQuery"/>（与显存采集共用），语义保持不变。
/// </summary>
public sealed class GpuUsageService : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _counter;

    public bool Available => _query.IsOpen && _counter != null;

    public GpuUsageService()
    {
        try
        {
            if (!_query.IsOpen) return;
            _counter = _query.AddDouble(@"\GPU Engine(*)\Utilization Percentage");
            if (_counter != null) _query.Collect(); // 首次采集建立基线（百分比是速率型计数器）
        }
        catch { }
    }

    /// <summary>本次采样：pid → GPU 占用百分比（各引擎求和，封顶 100）。不可用时返回空字典。</summary>
    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        if (_counter == null) return result;
        try
        {
            if (!_query.Collect()) return result;
            if (!_counter.Read(out var items)) return result;
            foreach (var item in items)
            {
                int pid = PdhQuery.ParsePid(item.Name);
                if (pid <= 0) continue;
                double v = item.Double;
                if (v < 0 || double.IsNaN(v)) continue;
                result[pid] = Math.Min(100.0, result.GetValueOrDefault(pid) + v);
            }
        }
        catch { }
        return result;
    }

    public void Dispose() => _query.Dispose();
}
