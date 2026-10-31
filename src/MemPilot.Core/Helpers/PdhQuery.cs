using System.Runtime.InteropServices;

namespace MemPilot.Helpers;

/// <summary>
/// PDH 查询薄封装（v0.3.4）：GPU% 与显存两部分共用一套 P/Invoke。
///
/// 为什么抽出来：原先 <see cref="MemPilot.Services.GpuUsageService"/> 自带 5 个 P/Invoke，
/// 再加显存计数器就要复制第二份。集中之后还有一个实在的好处——修掉「两次调用之间实例数变化
/// 导致整帧样本被丢弃」的老缺陷（PDH_MORE_DATA 时扩容重试，而不是放弃）。
///
/// 一律用 PdhAddEnglishCounterW：计数器路径用英文名，中文系统同样可用（本地化名会随语言变）。
/// </summary>
public sealed class PdhQuery : IDisposable
{
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    internal const int PDH_FMT_DOUBLE = 0x00000200;
    // 注意：PDH_FMT_LONG 是 0x100，PDH_FMT_LARGE 是 0x400。写成 0x100 会把 16GB 显存截成 2^31-1
    // （v0.3.4 实测：used=2147483647，一眼看是"整卡满"其实是 32 位溢出）。
    internal const int PDH_FMT_LARGE = 0x00000400;

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Double;
        [FieldOffset(8)] public long Long;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhItemW
    {
        public IntPtr Name;
        public PdhValue Value;
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

    private IntPtr _query;

    public bool IsOpen => _query != IntPtr.Zero;

    public PdhQuery()
    {
        try { PdhOpenQueryW(null, IntPtr.Zero, out _query); } catch { _query = IntPtr.Zero; }
    }

    /// <summary>加一个「百分比/速率」计数器（PDH_FMT_DOUBLE）。失败返回 null，调用方降级即可。</summary>
    public PdhCounter? AddDouble(string path) => Add(path, PDH_FMT_DOUBLE);

    /// <summary>加一个「原始字节计数」计数器（PDH_FMT_LARGE，int64 不丢精度）。</summary>
    public PdhCounter? AddLarge(string path) => Add(path, PDH_FMT_LARGE);

    private PdhCounter? Add(string path, int format)
    {
        if (!IsOpen) return null;
        try
        {
            if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var h) != 0 || h == IntPtr.Zero) return null;
            return new PdhCounter(h, format);
        }
        catch { return null; }
    }

    /// <summary>采集一轮。返回 false 表示整轮失败（调用方保留上次数据或降级）。</summary>
    public bool Collect()
    {
        if (!IsOpen) return false;
        try { return PdhCollectQueryData(_query) == 0; } catch { return false; }
    }

    public void Dispose()
    {
        try { if (_query != IntPtr.Zero) PdhCloseQuery(_query); } catch { }
        _query = IntPtr.Zero;
    }

    /// <summary>从实例名里取 pid：`pid_1234_luid_0x.._0x.._phys_0[_eng_0_engtype_3D]`。</summary>
    public static int ParsePid(string? instance)
    {
        if (string.IsNullOrEmpty(instance)) return 0;
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
}

/// <summary>一个 PDH 计数器句柄；<see cref="Read"/> 返回当前所有实例的格式化值。</summary>
public sealed class PdhCounter
{
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private readonly IntPtr _handle;
    private readonly int _format;

    internal PdhCounter(IntPtr handle, int format)
    {
        _handle = handle;
        _format = format;
    }

    public readonly struct Item
    {
        public Item(string name, double dbl, long lng) { Name = name; Double = dbl; Long = lng; }
        public string Name { get; }
        public double Double { get; }
        public long Long { get; }
    }

    /// <summary>
    /// 读取全部实例。实例数在两次调用之间变化时（PDH_MORE_DATA）扩容重试，
    /// 而不是像旧实现那样丢弃整帧——GPU 场景下进程频繁启停，丢帧会让曲线出现假空洞。
    /// </summary>
    public bool Read(out List<Item> items)
    {
        items = new List<Item>();
        var size = 0u;
        var count = 0u;
        var st = PdhQuery_GetArray(ref size, out count, IntPtr.Zero);
        if (st == 0 && size == 0) return true;               // 没有实例：合法的空结果
        if (st != PDH_MORE_DATA || size == 0) return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                st = PdhQuery_GetArray(ref size, out count, buf);
                if (st == PDH_MORE_DATA) continue;           // 实例数又变了：带上新 size 重试
                if (st != 0) return false;
                int itemSize = Marshal.SizeOf<PdhFormatItem>();
                for (int i = 0; i < count; i++)
                {
                    var raw = Marshal.PtrToStructure<PdhFormatItem>((IntPtr)(buf.ToInt64() + i * itemSize));
                    if (raw.Value.CStatus != 0) continue;
                    var name = Marshal.PtrToStringUni(raw.Name) ?? "";
                    items.Add(_format == PdhQuery.PDH_FMT_LARGE
                        ? new Item(name, 0, raw.Value.Long)
                        : new Item(name, raw.Value.Double, 0));
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return false;
    }

    private int PdhQuery_GetArray(ref uint size, out uint count, IntPtr buffer)
        => PdhGetFormattedCounterArrayW(_handle, _format, ref size, out count, buffer);

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFormatValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Double;
        [FieldOffset(8)] public long Long;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormatItem
    {
        public IntPtr Name;
        public PdhFormatValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, int format, ref uint bufferSize,
        out uint itemCount, IntPtr buffer);
}
