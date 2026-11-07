using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MemPilot.Helpers;

namespace MemPilot.Services;

public enum MemoryArea
{
    CombinedPageList,
    ModifiedFileCache,
    ModifiedPageList,
    RegistryCache,
    StandbyList,
    StandbyListLowPriority,
    SystemFileCache,
    /// <summary>页合并列表（Win8+，v0.9.0 补）。</summary>
    CombinedMemory,
    WorkingSet
}

public class NativeMemoryCleanerService
{
    // SystemMemoryListInformation = 80
    private const int SystemMemoryListInformation = 80;
    // SystemFileCacheInformation = 21
    private const int SystemFileCacheInformation = 21;
    // SystemCombinePhysicalMemoryInformation = 130（Win8+ 页合并列表，常在几个 GB 量级）
    private const int SystemCombinePhysicalMemoryInformation = 130;

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    // ---- 特权启用（v0.13.0，根因修复：0xC0000061 = STATUS_PRIVILEGE_NOT_HELD）----
    // 真机证据：IsAdmin() 为 true（角色是管理员）但 PurgeStandby 连续 4 次返回 0xC0000061。
    // IsAdmin 查的是**角色**，而 NtSetSystemInformation(SystemMemoryListInformation) 要求
    // SeProfileSingleProcessPrivilege 在令牌里**启用**（held ≠ enabled）。重装新 exe 后
    // 启动链不再带已启用的特权属性——这与 memreduct/RAMMap 先 AdjustTokenPrivileges 的做法一致。
    private const string SeProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;   // 单特权场景直接内联一项
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;

    private static bool _privilegesAttempted;
    /// <summary>特权启用结果（诊断用）：空 = 已成功或无需；否则是失败原因。</summary>
    public static string PrivilegeDiag { get; private set; } = "";

    // SystemMemoryListCommand enum values
    private enum SystemMemoryListCommand : int
    {
        MemoryCaptureAccessedBits = 0,
        MemoryEmptyWorkingSets = 1,
        MemoryFlushModifiedList = 2,
        MemoryPurgeStandbyList = 3,
        MemoryPurgeLowPriorityStandbyList = 4,
        MemoryCommandMax = 5
    }


    /// <summary>
    /// 启用 SeProfileSingleProcessPrivilege。幂等：进程生命周期内只尝试一次。
    /// 返回 true = 权限已可用（本来就启用或本次成功）。
    /// </summary>
    public static bool EnableSystemPrivileges()
    {
        if (_privilegesAttempted) return PrivilegeDiag.Length == 0;
        _privilegesAttempted = true;
        try
        {
            if (!LookupPrivilegeValue(null, SeProfileSingleProcessPrivilege, out var luid))
            {
                PrivilegeDiag = $"LookupPrivilegeValue 失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）";
                return false;
            }
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            {
                PrivilegeDiag = $"OpenProcessToken 失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）";
                return false;
            }
            try
            {
                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
                };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    PrivilegeDiag = $"AdjustTokenPrivileges 失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）——需要以管理员身份运行";
                    return false;
                }
                // ERROR_NOT_ALL_ASSIGNED (1300) 表示令牌不持有该特权——非管理员提权链常见
                PrivilegeDiag = System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 1300
                    ? "令牌未持有 SeProfileSingleProcessPrivilege（需要以管理员身份运行）"
                    : "";
                return PrivilegeDiag.Length == 0;
            }
            finally { CloseHandle(token); }
        }
        catch (Exception ex)
        {
            PrivilegeDiag = ex.Message;
            return false;
        }
    }

    /// <summary>NTSTATUS 主要代码 → 用户可读符号名（决策日志不再只亮十六进制）。</summary>
    private static string NtStatusName(int status) => status switch
    {
        unchecked((int)0xC0000061) => "STATUS_PRIVILEGE_NOT_HELD（需要管理员运行且启用 SeProfileSingleProcessPrivilege）",
        unchecked((int)0xC0000017) => "STATUS_NO_MEMORY",
        unchecked((int)0xC0000005) => "STATUS_ACCESS_VIOLATION",
        _ => $"NTSTATUS 0x{status:X8}",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_FILECACHE_INFORMATION
    {
        public IntPtr CurrentSize;
        public IntPtr PeakSize;
        public uint PageFaultCount;
        public IntPtr MinimumWorkingSet;
        public IntPtr MaximumWorkingSet;
        public IntPtr CurrentSizeIncludingTransitionInPages;
        public IntPtr PeakSizeIncludingTransitionInPages;
        public uint TransitionRePurposeCount;
        public uint Flags;
    }

    public bool IsAdmin()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public Dictionary<MemoryArea, (bool success, string message)> CleanAreas(List<MemoryArea> areas)
    {
        var results = new Dictionary<MemoryArea, (bool, string)>();
        foreach (var area in areas)
        {
            results[area] = CleanArea(area);
        }
        return results;
    }

    public (bool success, string message) CleanArea(MemoryArea area)
    {
        try
        {
            switch (area)
            {
                case MemoryArea.CombinedPageList:
                    return CleanCombinedPageList();
                case MemoryArea.ModifiedFileCache:
                    return CleanModifiedFileCache();
                case MemoryArea.ModifiedPageList:
                    return CleanModifiedPageList();
                case MemoryArea.RegistryCache:
                    return CleanRegistryCache();
                case MemoryArea.StandbyList:
                    return CleanStandbyList(false);
                case MemoryArea.StandbyListLowPriority:
                    return CleanStandbyList(true);
                case MemoryArea.SystemFileCache:
                    return CleanSystemFileCache();
                case MemoryArea.CombinedMemory:
                    return CleanCombinedMemory();
                case MemoryArea.WorkingSet:
                    return CleanWorkingSet();
                default:
                    return (false, "Unknown area");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private (bool, string) CleanCombinedPageList()
    {
        // Combined page list is part of standby list purging on Win8+
        // Use MemoryPurgeStandbyList which includes combined pages
        return SetMemoryListCommand(SystemMemoryListCommand.MemoryPurgeStandbyList, "Combined Page List");
    }

    private (bool, string) CleanModifiedFileCache()
    {
        // Flush volume caches for all fixed drives
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    // FlushFileBuffers via kernel32
                    var handle = CreateFile(drive.Name.TrimEnd('\\'), 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0x20000000, IntPtr.Zero); // FILE_FLAG_NO_BUFFERING
                    if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                    {
                        FlushFileBuffers(handle);
                        CloseHandle(handle);
                    }
                }
            }
            return (true, "Modified File Cache flushed");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private (bool, string) CleanModifiedPageList()
    {
        return SetMemoryListCommand(SystemMemoryListCommand.MemoryFlushModifiedList, "Modified Page List");
    }

    private (bool, string) CleanRegistryCache()
    {
        // Registry cache flush via NtSetSystemInformation with SystemMemoryListInformation
        // On Win8.1+, command 6 = MemoryFlushRegistry (not standard). Use flush modified list as fallback.
        try
        {
            // Attempt via undocumented command 6 (MemoryFlushRegistry)
            var cmd = 6;
            var ptr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(ptr, cmd);
                int status = NtSetSystemInformation(SystemMemoryListInformation, ptr, sizeof(int));
                if (status == 0) return (true, "Registry Cache flushed");
            }
            finally { Marshal.FreeHGlobal(ptr); }
            // Fallback
            return SetMemoryListCommand(SystemMemoryListCommand.MemoryFlushModifiedList, "Registry Cache (fallback)");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private (bool, string) CleanStandbyList(bool lowPriority)
    {
        var cmd = lowPriority ? SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList : SystemMemoryListCommand.MemoryPurgeStandbyList;
        var name = lowPriority ? "Standby List (Low Priority)" : "Standby List";
        return SetMemoryListCommand(cmd, name);
    }

    private (bool, string) CleanSystemFileCache()
    {
        try
        {
            var info = new SYSTEM_FILECACHE_INFORMATION();
            int size = Marshal.SizeOf<SYSTEM_FILECACHE_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                // Set minimum working set to a small value to trim cache
                info.MinimumWorkingSet = new IntPtr(1);
                info.MaximumWorkingSet = new IntPtr(1);
                Marshal.StructureToPtr(info, ptr, false);
                int status = NtSetSystemInformation(SystemFileCacheInformation, ptr, size);
                if (status == 0) return (true, "System File Cache trimmed");
                return (false, $"NtSetSystemInformation returned {status}");
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>v0.9.0：只压缩调用方给的安全子集；null 表示"全机"（仅保留给显式高危入口）。</summary>
    public void SetWorkingSetAllowList(IReadOnlyCollection<int>? pids) => _workingSetAllowList = pids;

    private IReadOnlyCollection<int>? _workingSetAllowList;

    /// <summary>
    /// 页合并列表清理（Win8+）。对标 memreduct 的 SystemCombinePhysicalMemoryInformation——
    /// 这块是"同内容页去重"的缓存，不回收时可能占几个 GB（v0.9.0 补，三轮复盘对标发现）。
    /// </summary>
    private (bool, string) CleanCombinedMemory()
    {
        try
        {
            // 结构长度为 0 即"清空合并列表"（Windows 语义：不要求传入参数）
            int status = NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, IntPtr.Zero, 0);
            if (status == 0) return (true, "Combined Memory (page combining) trimmed");
            return (false, $"NtSetSystemInformation returned {status}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private (bool, string) CleanWorkingSet()
    {
        int count = 0;
        // 三轮复盘 P0：旧实现遍历全部进程直接 EmptyWorkingSet（含游戏/系统/自己）。
        // 现在只有调用方显式给白名单时才动，未设置一律拒绝（fail-closed）。
        if (_workingSetAllowList == null)
            return (false, "未提供安全候选集，已拒绝执行（WorkingSet 清理需要显式进程名单）");
        foreach (var proc in Process.GetProcesses())
        {
            if (!_workingSetAllowList.Contains(proc.Id)) continue;
            try
            {
                var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | 0x0100, false, proc.Id);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        var psapi = NativeLibrary.Load("kernel32.dll");
                        var procAddr = NativeLibrary.GetExport(psapi, "K32EmptyWorkingSet");
                        var del = Marshal.GetDelegateForFunctionPointer<EmptyWsDelegate>(procAddr);
                        if (del(h)) count++;
                        NativeLibrary.Free(psapi);
                    }
                    catch { }
                    finally { NativeMethods.CloseHandle(h); }
                }
            }
            catch { }
        }
        return (true, $"Working Set emptied for {count} processes");
    }

    private (bool, string) SetMemoryListCommand(SystemMemoryListCommand cmd, string name)
    {
        try
        {
            // v0.13.0：调用前确保特权已启用（幂等）。0xC0000061 的根因修复点。
            EnableSystemPrivileges();
            var ptr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(ptr, (int)cmd);
                int status = NtSetSystemInformation(SystemMemoryListInformation, ptr, sizeof(int));
                if (status == 0) return (true, $"{name} cleaned");
                return (false, $"{name} 失败：{NtStatusName(status)}"
                               + (PrivilegeDiag.Length > 0 ? $"（{PrivilegeDiag}）" : ""));
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // P/Invoke for file cache flushing
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool EmptyWsDelegate(IntPtr hProcess);
}
