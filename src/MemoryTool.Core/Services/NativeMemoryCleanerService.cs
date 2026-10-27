using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MemoryTool.Helpers;

namespace MemoryTool.Services;

public enum MemoryArea
{
    CombinedPageList,
    ModifiedFileCache,
    ModifiedPageList,
    RegistryCache,
    StandbyList,
    StandbyListLowPriority,
    SystemFileCache,
    WorkingSet
}

public class NativeMemoryCleanerService
{
    // SystemMemoryListInformation = 80
    private const int SystemMemoryListInformation = 80;
    // SystemFileCacheInformation = 21
    private const int SystemFileCacheInformation = 21;

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

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

    private (bool, string) CleanWorkingSet()
    {
        int count = 0;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | 0x0100, false, proc.Id);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        var psapi = NativeLibrary.Load("psapi.dll");
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
            var ptr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(ptr, (int)cmd);
                int status = NtSetSystemInformation(SystemMemoryListInformation, ptr, sizeof(int));
                if (status == 0) return (true, $"{name} cleaned");
                return (false, $"NtSetSystemInformation returned {status} (0x{status:X})");
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
