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
        lock (_cacheLock) { _cache = (DateTime.UtcNow, result); }
        return result;
    }

    private List<GpuAdapterInfo> EnumerateAdapters()
    {
        var result = new List<GpuAdapterInfo>();
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-e2e40bd93d2c"); // IID_IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
                return result;
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
                    if ((desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE (WARP)
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
        }
        catch { }
        return result;
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

    [ComImport, Guid("770aae78-f26f-4dba-a829-e2e40bd93d2c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIFactory slots 0-4 (never called, stubs for vtable order)
        [PreserveSig] int EnumAdapters(int adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(int adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011c05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIAdapter slots 0-2
        [PreserveSig] int EnumOutputs(int output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid guid, out long umdVersion);
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport, Guid("645967A4-1392-4310-A798-8053CE3E93FD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3
    {
        // IDXGIAdapter1 slots 0-3 + IDXGIAdapter2 slot 4
        [PreserveSig] int EnumOutputs(int output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid guid, out long umdVersion);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        [PreserveSig] int GetDesc2(IntPtr pDesc);
        // IDXGIAdapter3
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
        public long LuidLowPart;
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
