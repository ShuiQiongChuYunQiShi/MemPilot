using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

public class ServiceInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public uint State { get; set; }
    public uint Type { get; set; }
    public int Pid { get; set; }
    public string BinaryPath { get; set; } = "";
    public bool IsDriver { get; set; }
}

public class ServiceService
{
    public List<ServiceInfo> EnumerateServices()
    {
        var list = new List<ServiceInfo>();
        var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ENUMERATE_SERVICE | NativeMethods.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return list;
        try
        {
            uint resume = 0;
            uint needed = 0, returned = 0;
            // first call to get size
            NativeMethods.EnumServicesStatusEx(scm, 0, 0xFFFFFFFF, 0xFFFFFFFF, IntPtr.Zero, 0, out needed, out returned, ref resume, null);
            if (needed == 0) return list;
            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                resume = 0;
                if (NativeMethods.EnumServicesStatusEx(scm, 0, 0xFFFFFFFF, 0xFFFFFFFF, buf, needed, out needed, out returned, ref resume, null))
                {
                    int structSize = Marshal.SizeOf<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>();
                    IntPtr ptr = buf;
                    for (int i = 0; i < returned; i++)
                    {
                        var s = Marshal.PtrToStructure<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>(ptr);
                        var info = new ServiceInfo
                        {
                            Name = Marshal.PtrToStringAuto(s.lpServiceName) ?? "",
                            DisplayName = Marshal.PtrToStringAuto(s.lpDisplayName) ?? "",
                            State = s.dwCurrentState,
                            Type = s.dwServiceType,
                            Pid = (int)s.dwProcessId,
                            IsDriver = (s.dwServiceType & 0x0000000B) != 0 // kernel/file system driver
                        };
                        // query config for binary path
                        var hSvc = NativeMethods.OpenService(scm, info.Name, NativeMethods.SERVICE_QUERY_CONFIG);
                        if (hSvc != IntPtr.Zero)
                        {
                            try
                            {
                                uint cfgNeeded = 0;
                                NativeMethods.QueryServiceConfig(hSvc, IntPtr.Zero, 0, out cfgNeeded);
                                if (cfgNeeded > 0)
                                {
                                    var cfgBuf = Marshal.AllocHGlobal((int)cfgNeeded);
                                    try
                                    {
                                        if (NativeMethods.QueryServiceConfig(hSvc, cfgBuf, cfgNeeded, out _))
                                        {
                                            var cfg = Marshal.PtrToStructure<NativeMethods.QUERY_SERVICE_CONFIG>(cfgBuf);
                                            info.BinaryPath = Marshal.PtrToStringAuto(cfg.lpBinaryPathName) ?? "";
                                        }
                                    }
                                    finally { Marshal.FreeHGlobal(cfgBuf); }
                                }
                            }
                            finally { NativeMethods.CloseServiceHandle(hSvc); }
                        }
                        list.Add(info);
                        ptr += structSize;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { NativeMethods.CloseServiceHandle(scm); }
        return list;
    }

    public Dictionary<int, List<ServiceInfo>> MapByPid(List<ServiceInfo> services)
    {
        return services.GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.ToList());
    }
}
