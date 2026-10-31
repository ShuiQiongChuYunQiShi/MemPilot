using System.Runtime.InteropServices;

namespace MemPilot.Services;

/// <summary>
/// 逐进程 GPU 使用率（PDH \GPU Engine(*)）——区分"游戏/视频渲染/3D 工作"与普通应用的核心信号。
/// 尽力而为：PDH 不可用（无 GPU/计数器被禁用）时 Available=false，判定层必须把 GPU 当"不可用"
/// 处理（重分配权重），绝不当 0 分。
/// </summary>
public sealed class GpuUsageService : IDisposable
{
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private const int PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, int format, ref uint bufferSize,
        out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    private IntPtr _query = IntPtr.Zero;
    private IntPtr _counter = IntPtr.Zero;
    private bool _failed;

    public bool Available => !_failed && _counter != IntPtr.Zero;

    public GpuUsageService()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _failed = true; return; }
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
            { _failed = true; return; }
            PdhCollectQueryData(_query); // 首次采集建立基线
        }
        catch { _failed = true; }
    }

    /// <summary>本次采样：pid → GPU 占用百分比（各引擎求和，封顶 100）。不可用时返回空字典。</summary>
    public Dictionary<int, double> Sample()
    {
        var result = new Dictionary<int, double>();
        if (!Available) return result;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return result;
            uint size = 0, count = 0;
            int st = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
            if (st != PDH_MORE_DATA || size == 0) return result;

            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0 || count == 0)
                    return result;
                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>((IntPtr)(buf.ToInt64() + i * itemSize));
                    if (item.FmtValue.CStatus != 0) continue;
                    var name = Marshal.PtrToStringUni(item.szName) ?? "";
                    int pid = ParsePid(name);
                    if (pid <= 0) continue;
                    double v = item.FmtValue.doubleValue;
                    if (v < 0) v = 0;
                    result[pid] = Math.Min(100.0, result.GetValueOrDefault(pid) + v);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return result;
    }

    /// <summary>实例名形如 pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D。</summary>
    private static int ParsePid(string instance)
    {
        try
        {
            int i = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            int start = i + 4, end = instance.IndexOf('_', start);
            if (end < 0) end = instance.Length;
            return int.TryParse(instance[start..end], out var pid) ? pid : 0;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        try { if (_query != IntPtr.Zero) PdhCloseQuery(_query); } catch { }
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }
}
