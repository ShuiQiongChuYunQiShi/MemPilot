using MemPilot.Helpers;

namespace MemPilot.Services;

/// <summary>
/// GPU 显存读取（v0.3.4）：逐进程专用/共享显存 + 适配器级已用量，全部走 PDH。
///
/// 为什么不用 DXGI：`CreateDXGIFactory1` 在某些执行上下文直接失败（本机实测 E_NOINTERFACE），
/// 而 `\GPU Process Memory(*)` / `\GPU Adapter Memory(*)` 计数器一直可用。DXGI 仍保留在
/// <see cref="GpuInfoService"/> 里作为首选来源（它还能给出预算/保留量），这里是稳定的兜底。
///
/// 语义约定（与 GPU% 一致）：读不到就是"不可用"，绝不返回 0 冒充真实值。
/// </summary>
public sealed class GpuMemoryService : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _processDedicated;
    private readonly PdhCounter? _processShared;
    private readonly PdhCounter? _adapterDedicated;

    public bool ProcessAvailable => _query.IsOpen && _processDedicated != null;
    public bool AdapterAvailable => _query.IsOpen && _adapterDedicated != null;
    public bool Available => ProcessAvailable || AdapterAvailable;

    /// <summary>最近一次读取失败的原因（诊断用；成功时清空）。</summary>
    public string LastDiag { get; private set; } = "";

    public GpuMemoryService()
    {
        try
        {
            if (!_query.IsOpen) { LastDiag = "pdh: query open failed"; return; }
            // 字节计数器是原始型，不需要基线，第一次 Collect 即可读值
            _processDedicated = _query.AddLarge(@"\GPU Process Memory(*)\Local Usage");
            _processShared = _query.AddLarge(@"\GPU Process Memory(*)\Non Local Usage");
            _adapterDedicated = _query.AddLarge(@"\GPU Adapter Memory(*)\Dedicated Usage");
            if (!ProcessAvailable && !AdapterAvailable) LastDiag = "pdh: gpu memory counters unavailable";
        }
        catch (Exception ex) { LastDiag = "pdh: " + ex.Message; }
    }

    /// <summary>一次采样（内含一轮 Collect）。不可用时各字段为空/0，调用方据 Available 判断。</summary>
    public GpuMemorySnapshot Sample()
    {
        var snap = new GpuMemorySnapshot();
        if (!_query.IsOpen) return snap;

        try
        {
            if (!_query.Collect()) { LastDiag = "pdh: collect failed"; return snap; }
            LastDiag = "";

            if (_processDedicated != null && _processDedicated.Read(out var locals))
            {
                snap.ProcessDedicatedAvailable = true;
                foreach (var item in locals)
                {
                    int pid = PdhQuery.ParsePid(item.Name);
                    if (pid <= 0 || item.Long <= 0) continue;
                    snap.ProcessDedicated[pid] = snap.ProcessDedicated.GetValueOrDefault(pid) + item.Long;
                }
            }

            if (_processShared != null && _processShared.Read(out var shared))
            {
                foreach (var item in shared)
                {
                    int pid = PdhQuery.ParsePid(item.Name);
                    if (pid <= 0 || item.Long <= 0) continue;
                    snap.ProcessShared[pid] = snap.ProcessShared.GetValueOrDefault(pid) + item.Long;
                }
            }

            if (_adapterDedicated != null && _adapterDedicated.Read(out var adapters))
            {
                snap.AdapterAvailable = true;
                foreach (var item in adapters)
                {
                    if (item.Long <= 0) continue;
                    snap.AdapterDedicatedByInstance[item.Name] = item.Long;
                    snap.AdapterDedicatedBytes += item.Long;
                }
            }
        }
        catch (Exception ex) { LastDiag = "pdh: " + ex.Message; }
        return snap;
    }

    public void Dispose() => _query.Dispose();
}

/// <summary>一次显存采样结果。UsedBytes 优先取适配器级（权威），退而用逐进程合计。</summary>
public sealed class GpuMemorySnapshot
{
    public bool ProcessDedicatedAvailable { get; set; }
    public bool AdapterAvailable { get; set; }
    public bool Available => ProcessDedicatedAvailable || AdapterAvailable;

    public Dictionary<int, long> ProcessDedicated { get; } = new();
    public Dictionary<int, long> ProcessShared { get; } = new();
    public Dictionary<string, long> AdapterDedicatedByInstance { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long AdapterDedicatedBytes { get; set; }

    public long ProcessDedicatedSum => ProcessDedicated.Values.Sum();
    public long ProcessSharedSum => ProcessShared.Values.Sum();

    /// <summary>样本里记录的已用量；不可用返回 null（禁止用 0 冒充）。</summary>
    public long? UsedBytes => AdapterAvailable ? AdapterDedicatedBytes
                             : ProcessDedicatedAvailable ? ProcessDedicatedSum
                             : null;

    /// <summary>来源标记：adapter / process / none（历史页与诊断据此说明"这个数字从哪来"）。</summary>
    public string Source => AdapterAvailable ? "pdh-adapter"
                          : ProcessDedicatedAvailable ? "pdh-process"
                          : "none";
}
