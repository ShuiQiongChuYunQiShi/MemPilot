using System.Runtime.InteropServices;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// GPU adapter info via DXGI: total dedicated VRAM per adapter (always available)
/// plus current usage when IDXGIAdapter3 QI succeeds. Usage is informational context
/// for AI-generation scenarios; failures degrade to totals only.
/// </summary>
public class GpuInfoService
{
    private static (DateTime time, List<GpuAdapterInfo> data)? _cache;
    private static readonly object _cacheLock = new();

    public List<GpuAdapterInfo> GetAdapters()
    {
        lock (_cacheLock)
        {
            if (_cache != null && (DateTime.UtcNow - _cache.Value.time).TotalSeconds < 60)
                return _cache.Value.data;
        }
        var result = EnumerateAdapters();
        if (result.Count > 0) // 空结果不缓存：下一轮立刻重试（DXGI+GDI 每次仅几毫秒）
        {
            lock (_cacheLock) { _cache = (DateTime.UtcNow, result); }
        }
        return result;
    }

    /// <summary>最近一次枚举的诊断信息（设置页/测试可读，失败不再无声）。</summary>
    public static string LastDiag { get; private set; } = "none";

    private List<GpuAdapterInfo> EnumerateAdapters()
    {
        var result = new List<GpuAdapterInfo>();
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-e2e40bd93d2c"); // IID_IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
            {
                // 关键：不能在这里 return —— 那样会绕过下面的 GDI 兜底（v0.2.2 的兜底因此是死代码）
                LastDiag = "dxgi: factory creation failed";
            }
            else
            {
            try
            {
                var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
                for (int i = 0; ; i++)
                {
                    IDXGIAdapter1? adapter = null;
                    int hr = 0;
                    try { hr = factory.EnumAdapters1(i, out adapter); } catch { break; }
                    if (hr != 0 || adapter == null) break;
                    if (adapter.GetDesc1(out var desc) != 0) { Marshal.ReleaseComObject(adapter); continue; }
                    if ((desc.Flags & 2) != 0) { Marshal.ReleaseComObject(adapter); continue; } // DXGI_ADAPTER_FLAG_SOFTWARE (WARP)
                    var info = new GpuAdapterInfo
                    {
                        Name = desc.Description,
                        Vendor = VendorName(desc.VendorId),
                        DedicatedVideoMemoryBytes = (long)desc.DedicatedVideoMemory
                    };
                    try
                    {
                        if (adapter is IDXGIAdapter3 adapter3 &&
                            adapter3.QueryVideoMemoryInfo(0 /*DXGI_MEMORY_SEGMENT_GROUP_LOCAL*/, out var vmem) == 0)
                            info.UsedVideoMemoryBytes = (long)vmem.CurrentUsage;
                    }
                    catch { }
                    result.Add(info);
                    Marshal.ReleaseComObject(adapter);
                }
            }
            finally { Marshal.Release(factoryPtr); }
            LastDiag = result.Count == 0 ? "dxgi: no adapters" : "dxgi: ok";
            }
        }
        catch (Exception ex) { LastDiag = "dxgi: " + ex.Message; }
        if (result.Count == 0)
        {
            // 2026-10-31 真机：某些执行上下文 DXGI 工厂创建失败（E_NOINTERFACE），
            // 但 GDI/注册表始终可用 —— 名称与显存走兜底，用量留空（宁缺勿假）。
            result = EnumerateViaDisplayApi();
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;

    private static List<GpuAdapterInfo> EnumerateViaDisplayApi()
    {
        var list = new List<GpuAdapterInfo>();
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
                // 只排除镜像驱动与基础渲染器；不再要求"附着到当前桌面"（远程/虚拟会话会全滤掉真显卡）
                if ((dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
                var name = (dd.DeviceString ?? "").Trim();
                if (name.Length == 0 || name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                if (list.Any(x => x.Name == name)) continue;
                list.Add(new GpuAdapterInfo
                {
                    Name = name,
                    Vendor = GuessVendor(name),
                    DedicatedVideoMemoryBytes = ReadVramFromRegistry(name)
                });
            }
            LastDiag += " | gdi: " + list.Count;
        }
        catch (Exception ex) { LastDiag += " | gdi: " + ex.Message; }
        return list;
    }

    private static string GuessVendor(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
        return "";
    }

    /// <summary>显存大小：显示适配器类注册表 HardwareInformation.qwMemorySize（REG_QWORD，64 位可靠）。</summary>
    private static long ReadVramFromRegistry(string adapterName)
    {
        try
        {
            const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(classKey);
            if (baseKey == null) return 0;
            foreach (var sub in baseKey.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var dev = baseKey.OpenSubKey(sub);
                if (dev == null) continue;
                var desc = dev.GetValue("DriverDesc") as string ?? "";
                if (!adapterName.Contains(desc, StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains(adapterName, StringComparison.OrdinalIgnoreCase)) continue;
                if (dev.GetValue("HardwareInformation.qwMemorySize") is long vram && vram > 0) return vram;
            }
        }
        catch { }
        return 0;
    }

    private static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => ""
    };

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    // COM 互操作要点：vtable 顺序 = 基接口方法在前。必须把基接口声明出来并继承，
    // 否则方法序号整体错位（2026-10-31 真机 bug：EnumAdapters1 打到 CreateSwapChain 槽，
    // 枚举永远 0 个适配器 → "GPU 信息不可用"）。
    [ComImport, Guid("aec22fb8-76f3-4639-9be0-28eb43a67a2e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIObject
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
    }

    [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory : IDXGIObject
    {
        [PreserveSig] int EnumAdapters(int adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-e2e40bd93d2c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1 : IDXGIFactory
    {
        [PreserveSig] int EnumAdapters1(int adapter, out IDXGIAdapter1 ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("2411e6fe-f28a-4b3e-abf1-93a21a421c33"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter : IDXGIObject
    {
        [PreserveSig] int EnumOutputs(int output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid guid, out long umdVersion);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011c05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1 : IDXGIAdapter
    {
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport, Guid("0aa1ae0a-fa0e-4b84-8644-e05ff8e5acb5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter2 : IDXGIAdapter1
    {
        [PreserveSig] int GetDesc2(IntPtr pDesc);
    }

    [ComImport, Guid("645967A4-1392-4310-A798-8053CE3E93FD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3 : IDXGIAdapter2
    {
        [PreserveSig] int RegisterVideoMemoryBudgetChangeNotificationEvent(IntPtr hEvent, out uint cookie);
        [PreserveSig] int UnregisterVideoMemoryBudgetChangeNotificationEvent(uint cookie);
        [PreserveSig] int QueryVideoMemoryInfo(uint segmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
        [PreserveSig] int SetVideoMemoryReservation(uint segmentGroup, ref DXGI_QUERY_VIDEO_MEMORY_INFO reservation);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}
