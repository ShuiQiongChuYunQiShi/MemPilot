using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MemoryTool.Helpers;
using MemoryTool.Models;

namespace MemoryTool.Services;

public class ProcessService
{
    private static readonly Dictionary<string, (DateTime lastWrite, string company, string product, string description, string version, bool signed, string subject, string issuer)> _sigCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _sigLock = new();
    public SystemMemoryInfo GetSystemMemory()
    {
        var ms = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref ms);
        return new SystemMemoryInfo
        {
            TotalPhysicalBytes = (long)ms.ullTotalPhys,
            AvailablePhysicalBytes = (long)ms.ullAvailPhys,
            TotalCommitLimitBytes = (long)ms.ullTotalPageFile,
            TotalCommittedBytes = (long)ms.ullTotalPageFile - (long)ms.ullAvailPageFile,
            MemoryLoadPercent = ms.dwMemoryLoad
        };
    }

    public List<ProcessInfo> EnumerateProcesses()
    {
        var result = new List<ProcessInfo>();
        var startupService = new StartupService();
        var startupItems = startupService.Enumerate();
        var scheduledService = new ScheduledTaskService();
        var scheduledTasks = scheduledService.Enumerate();
        var procs = Process.GetProcesses();
        // parent map via NtQuery
        var parentMap = new Dictionary<int,int>();
        foreach (var p in procs)
        {
            parentMap[p.Id] = GetParentPid(p.Id);
        }
        // tcp counts
        var tcpCounts = GetTcpConnectionCountsByPid();
        // visible windows
        var windowPids = GetVisibleWindowPids();

        foreach (var p in procs)
        {
            var info = new ProcessInfo { Pid = p.Id, Name = p.ProcessName };
            try
            {
                info.ParentPid = parentMap.TryGetValue(p.Id, out var pp) ? pp : 0;
                try { info.StartTime = p.StartTime; } catch { info.StartTime = DateTime.MinValue; info.AccessDenied = true; }
                try { info.Memory.ThreadCount = p.Threads.Count; } catch { }
                try { info.Memory.HandleCount = p.HandleCount; } catch { }
                try { info.MainWindowTitle = p.MainWindowTitle; } catch { }
                info.HasVisibleWindow = windowPids.Contains(p.Id) || !string.IsNullOrEmpty(info.MainWindowTitle);

                // path & command line
                try { info.ExecutablePath = p.MainModule?.FileName ?? ""; } catch { info.AccessDenied = true; info.AccessNote = "MainModule access denied"; }
                info.CommandLine = GetCommandLine(p.Id);
                info.IsStartupItem = startupService.IsStartupPath(info.ExecutablePath, startupItems);
                info.IsScheduledTask = scheduledService.IsScheduledTaskProcess(info.ExecutablePath, scheduledTasks);

                // version info (cached)
                if (!string.IsNullOrEmpty(info.ExecutablePath) && File.Exists(info.ExecutablePath))
                {
                    var fi = new FileInfo(info.ExecutablePath);
                    lock (_sigLock)
                    {
                        if (_sigCache.TryGetValue(info.ExecutablePath, out var cached) && cached.lastWrite == fi.LastWriteTimeUtc)
                        {
                            info.Company = cached.company;
                            info.ProductName = cached.product;
                            info.FileDescription = cached.description;
                            info.FileVersion = cached.version;
                            info.IsSigned = cached.signed;
                            info.SignatureSubject = cached.subject;
                            info.SignatureIssuer = cached.issuer;
                        }
                        else
                        {
                            try
                            {
                                var vi = FileVersionInfo.GetVersionInfo(info.ExecutablePath);
                                info.Company = vi.CompanyName ?? "";
                                info.ProductName = vi.ProductName ?? "";
                                info.FileDescription = vi.FileDescription ?? "";
                                info.FileVersion = vi.FileVersion ?? "";
                            }
                            catch { }
                            try
                            {
                                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(info.ExecutablePath);
                                info.IsSigned = true;
                                info.SignatureSubject = cert.Subject;
                                info.SignatureIssuer = cert.Issuer;
                            }
                            catch { info.IsSigned = false; }
                            _sigCache[info.ExecutablePath] = (fi.LastWriteTimeUtc, info.Company, info.ProductName, info.FileDescription, info.FileVersion, info.IsSigned, info.SignatureSubject, info.SignatureIssuer);
                        }
                    }
                }

                // memory counters
                CollectMemory(p, info);
                // io counters
                CollectIo(p.Id, info);
                if (tcpCounts.TryGetValue(p.Id, out var tcpCount)) info.Memory.NetworkConnections = tcpCount;
                // cpu
                try
                {
                    info.Memory.TotalCpuTime = p.TotalProcessorTime;
                }
                catch { }
                // tcp
                if (tcpCounts.TryGetValue(p.Id, out var tc)) { /* store in memory? add field later; use LastActivity */ }
                // threads
                try
                {
                    foreach (ProcessThread t in p.Threads)
                    {
                        info.Threads.Add(new ThreadInfo
                        {
                            Tid = t.Id,
                            OwnerPid = p.Id,
                            State = t.ThreadState,
                            WaitReason = t.WaitReason,
                            Priority = t.BasePriority,
                            TotalCpuTime = t.TotalProcessorTime,
                            StartTime = SafeStartTime(t)
                        });
                    }
                }
                catch { }

                info.ChildPids = procs.Where(x => parentMap.TryGetValue(x.Id,out var par) && par == p.Id).Select(x=>x.Id).ToList();

                // idle time
                if (info.StartTime != DateTime.MinValue)
                {
                    // CPU-based idle: if total cpu time small relative; approximate idle minutes since start with no cpu? We'll compute in detector. Set IdleTime to now-start as placeholder? Instead set 0.
                }
                info.Memory.LastActivityUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                info.AccessDenied = true;
                info.AccessNote = ex.Message;
            }
            result.Add(info);
        }
        return result;
    }

    private void CollectMemory(Process p, ProcessInfo info)
    {
        try
        {
            var m = info.Memory;
            m.WorkingSetBytes = p.WorkingSet64;
            m.PrivateBytes = p.PrivateMemorySize64;
            m.CommitBytes = p.PagedMemorySize64; // approx
            m.PeakWorkingSetBytes = p.PeakWorkingSet64;
            m.PagedPoolBytes = p.PagedSystemMemorySize64;
            m.NonPagedPoolBytes = p.NonpagedSystemMemorySize64;
            m.VirtualBytes = p.VirtualMemorySize64;
            m.PeakVirtualBytes = p.PeakVirtualMemorySize64;
            m.ThreadCount = p.Threads.Count;
        }
        catch { info.AccessDenied = true; }

        // PSAPI detailed
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var counters = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX2();
                    counters.cb = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX2>();
                    if (NativeMethods.K32GetProcessMemoryInfo(h, ref counters, counters.cb))
                    {
                        info.Memory.WorkingSetBytes = (long)counters.WorkingSetSize;
                        info.Memory.PeakWorkingSetBytes = (long)counters.PeakWorkingSetSize;
                        info.Memory.PrivateWorkingSetBytes = (long)counters.PrivateWorkingSetSize;
                        info.Memory.SharedCommitBytes = (long)counters.SharedCommitUsage;
                        info.Memory.CommitBytes = (long)counters.PrivateUsage; // commit (pagefile usage)
                        info.Memory.PagedPoolBytes = (long)counters.QuotaPagedPoolUsage;
                        info.Memory.NonPagedPoolBytes = (long)counters.QuotaNonPagedPoolUsage;
                    }
                }
                finally { NativeMethods.CloseHandle(h); }
            }
        }
        catch { }
    }

    private void CollectIo(int pid, ProcessInfo info)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    if (NativeMethods.GetProcessIoCounters(h, out var io))
                    {
                        info.Memory.IoReadBytes = (long)io.ReadTransferCount;
                        info.Memory.IoWriteBytes = (long)io.WriteTransferCount;
                        info.Memory.IoReadOps = (long)io.ReadOperationCount;
                        info.Memory.IoWriteOps = (long)io.WriteOperationCount;
                    }
                }
                finally { NativeMethods.CloseHandle(h); }
            }
        }
        catch { }
    }

    private int GetParentPid(int pid)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                var pbi = new NativeMethods.PROCESS_BASIC_INFORMATION();
                int ret;
                int status = NativeMethods.NtQueryInformationProcess(h, (int)NativeMethods.PROCESSINFOCLASS.ProcessBasicInformation, ref pbi, Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out ret);
                if (status == 0)
                {
                    return pbi.InheritedFromUniqueProcessId.ToInt32();
                }
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { }
        return 0;
    }

    private string GetCommandLine(int pid)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                // ProcessCommandLineInformation = 60, returns UNICODE_STRING
                int bufSize = 0x10000;
                var buf = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int status = NativeMethods.NtQueryInformationProcess(h, 60, buf, bufSize, out int ret);
                    if (status == 0 && ret >= 16)
                    {
                        // UNICODE_STRING: Length (ushort), MaximumLength (ushort), Buffer (ptr)
                        int length = Marshal.ReadInt16(buf);
                        IntPtr strPtr = Marshal.ReadIntPtr(buf, 8); // on 64-bit offset 8
                        if (strPtr != IntPtr.Zero && length > 0)
                        {
                            return Marshal.PtrToStringUni(strPtr, length / 2) ?? "";
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { }
        return "";
    }

    private DateTime SafeStartTime(ProcessThread t)
    {
        try { return t.StartTime; } catch { return DateTime.MinValue; }
    }

    private HashSet<int> GetVisibleWindowPids()
    {
        var set = new HashSet<int>();
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != 0) set.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set;
    }

    private Dictionary<int,int> GetTcpConnectionCountsByPid()
    {
        var dict = new Dictionary<int,int>();
        try
        {
            uint size = 0;
            NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
            if (size == 0) return dict;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint ret = NativeMethods.GetExtendedTcpTable(buf, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret == 0)
                {
                    // first 4 bytes: numEntries
                    int num = Marshal.ReadInt32(buf);
                    int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                    IntPtr rowPtr = buf + 4;
                    for (int i = 0; i < num; i++)
                    {
                        var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                        if (!dict.ContainsKey((int)row.dwOwningPid)) dict[(int)row.dwOwningPid] = 0;
                        dict[(int)row.dwOwningPid]++;
                        rowPtr += rowSize;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return dict;
    }

    public void TerminateProcess(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, pid);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess failed");
        try
        {
            if (!NativeMethods.TerminateProcess(h, 0)) throw new Exception("TerminateProcess failed");
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    public void SuspendProcess(int pid, List<int> tids)
    {
        foreach (var tid in tids)
        {
            var h = NativeMethods.OpenThread(NativeMethods.THREAD_SUSPEND_RESUME, false, (uint)tid);
            if (h != IntPtr.Zero)
            {
                NativeMethods.SuspendThread(h);
                NativeMethods.CloseHandle(h);
            }
        }
    }

    public void ResumeProcess(int pid, List<int> tids)
    {
        foreach (var tid in tids)
        {
            var h = NativeMethods.OpenThread(NativeMethods.THREAD_SUSPEND_RESUME, false, (uint)tid);
            if (h != IntPtr.Zero)
            {
                NativeMethods.ResumeThread(h);
                NativeMethods.CloseHandle(h);
            }
        }
    }

    public void TrimWorkingSet(int pid)
    {
        // EmptyWorkingSet via psapi
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | 0x0100, false, pid); // PROCESS_SET_QUOTA = 0x0100
            if (h != IntPtr.Zero)
            {
                try
                {
                    // EmptyWorkingSet export from psapi
                    var psapi = NativeLibrary.Load("psapi.dll");
                    var proc = NativeLibrary.GetExport(psapi, "K32EmptyWorkingSet");
                    var del = Marshal.GetDelegateForFunctionPointer<EmptyWsDelegate>(proc);
                    del(h);
                    NativeLibrary.Free(psapi);
                }
                catch { }
                finally { NativeMethods.CloseHandle(h); }
            }
        }
        catch { }
    }

    public void SuspendThread(int tid)
    {
        var h = NativeMethods.OpenThread(NativeMethods.THREAD_SUSPEND_RESUME, false, (uint)tid);
        if (h != IntPtr.Zero)
        {
            NativeMethods.SuspendThread(h);
            NativeMethods.CloseHandle(h);
        }
    }

    public void ResumeThread(int tid)
    {
        var h = NativeMethods.OpenThread(NativeMethods.THREAD_SUSPEND_RESUME, false, (uint)tid);
        if (h != IntPtr.Zero)
        {
            NativeMethods.ResumeThread(h);
            NativeMethods.CloseHandle(h);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool EmptyWsDelegate(IntPtr hProcess);
}
