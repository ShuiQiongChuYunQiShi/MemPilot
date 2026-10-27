using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MemoryTool.Helpers;
using MemoryTool.Models;

namespace MemoryTool.Services;

public class ProcessService
{
    private readonly TrustVerifier _trust = new();

    // ---- caches: scheduled tasks & startup items rarely change; do not spawn schtasks every refresh ----
    private static (DateTime time, List<ScheduledTaskInfo> tasks)? _taskCache;
    private static (DateTime time, List<StartupItem> items)? _startupCache;
    private static readonly object _cacheLock = new();
    private static readonly Dictionary<int, string> _cmdlineCache = new();
    private static readonly object _cmdlineLock = new();

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
        var startupItems = GetStartupItemsCached();
        var scheduledTasks = GetScheduledTasksCached();
        var procs = Process.GetProcesses();
        var parentMap = new Dictionary<int, int>();
        foreach (var p in procs) parentMap[p.Id] = GetParentPid(p.Id);
        var tcpCounts = GetTcpConnectionCountsByPid();
        var windowPids = GetVisibleWindowPids();
        var startTimes = new Dictionary<int, DateTime>();
        foreach (var p in procs)
        {
            try { startTimes[p.Id] = p.StartTime; } catch { }
        }

        foreach (var p in procs)
        {
            var info = new ProcessInfo { Pid = p.Id, Name = p.ProcessName };
            try
            {
                info.ParentPid = parentMap.TryGetValue(p.Id, out var pp) ? pp : 0;
                try { info.StartTime = p.StartTime; } catch { info.StartTime = DateTime.MinValue; info.AccessDenied = true; }
                try { info.Memory.HandleCount = p.HandleCount; } catch { }

                // path: QueryFullProcessImageName works for elevated/system processes with a limited handle
                info.ExecutablePath = QueryImagePath(p.Id);

                try { info.CommandLine = GetCommandLineCached(p.Id); } catch { }

                // visible window + title
                if (windowPids.TryGetValue(p.Id, out var title))
                {
                    info.HasVisibleWindow = true;
                    info.MainWindowTitle = title;
                }
                try { if (string.IsNullOrEmpty(info.MainWindowTitle)) info.MainWindowTitle = p.MainWindowTitle ?? ""; } catch { }

                info.IsStartupItem = StartupService.IsStartupPath(info.ExecutablePath, startupItems);
                info.IsScheduledTask = ScheduledTaskService.IsScheduledTaskProcess(info.ExecutablePath, scheduledTasks);

                // file metadata (fast) + trust (cached by path)
                if (!string.IsNullOrEmpty(info.ExecutablePath) && File.Exists(info.ExecutablePath))
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
                    var (verified, subject, issuer) = _trust.Verify(info.ExecutablePath);
                    info.SignatureVerified = verified;
                    info.IsSigned = verified;
                    info.SignatureSubject = subject;
                    info.SignatureIssuer = issuer;
                }

                CollectMemory(p, info);
                CollectIo(p.Id, info);
                if (tcpCounts.TryGetValue(p.Id, out var tcpCount)) info.Memory.NetworkConnections = tcpCount;
                try { info.Memory.TotalCpuTime = p.TotalProcessorTime; } catch { }

                // threads: enumerate only lightweight state; heavy listing is on-demand
                try
                {
                    info.Memory.ThreadCount = p.Threads.Count;
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

                info.ChildPids = procs.Where(x => parentMap.TryGetValue(x.Id, out var par) && par == p.Id).Select(x => x.Id).ToList();
                info.Memory.LastActivityUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                info.AccessDenied = true;
                info.AccessNote = ex.Message;
            }
            result.Add(info);
        }
        TrimCmdlineCache(startTimes);
        return result;
    }

    private void CollectMemory(Process p, ProcessInfo info)
    {
        try
        {
            var m = info.Memory;
            m.WorkingSetBytes = p.WorkingSet64;
            m.PrivateBytes = p.PrivateMemorySize64;
            m.PeakWorkingSetBytes = p.PeakWorkingSet64;
            m.VirtualBytes = p.VirtualMemorySize64;
            m.PeakVirtualBytes = p.PeakVirtualMemorySize64;
        }
        catch { info.AccessDenied = true; }

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
                        info.Memory.CommitBytes = (long)counters.PrivateUsage;
                        info.Memory.PrivateBytes = (long)counters.PrivateUsage; // commit charge, single source of truth
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

    private static string QueryImagePath(int pid)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try { return NativeMethods.GetProcessImageNameWin32(h); }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { return ""; }
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
                int status = NativeMethods.NtQueryInformationProcess(h, (int)NativeMethods.PROCESSINFOCLASS.ProcessBasicInformation, ref pbi, Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _);
                if (status == 0) return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { }
        return 0;
    }

    private static string GetCommandLineCached(int pid)
    {
        lock (_cmdlineLock)
        {
            if (_cmdlineCache.TryGetValue(pid, out var cached)) return cached;
        }
        string value = QueryCommandLine(pid);
        lock (_cmdlineLock)
        {
            _cmdlineCache[pid] = value;
        }
        return value;
    }

    private static string QueryCommandLine(int pid)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                // ProcessCommandLineInformation (60): two-call pattern, then read UNICODE_STRING
                int bufSize = 0x1000;
                var buf = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int status = NativeMethods.NtQueryInformationProcess(h, 60, buf, bufSize, out int ret);
                    if (status != 0 && ret > bufSize)
                    {
                        Marshal.FreeHGlobal(buf);
                        bufSize = ret;
                        buf = Marshal.AllocHGlobal(bufSize);
                        status = NativeMethods.NtQueryInformationProcess(h, 60, buf, bufSize, out ret);
                    }
                    if (status == 0 && ret >= 16)
                    {
                        int length = Marshal.ReadInt16(buf);
                        IntPtr strPtr = Marshal.ReadIntPtr(buf, 8);
                        if (strPtr != IntPtr.Zero && length > 0)
                            return Marshal.PtrToStringUni(strPtr, length / 2) ?? "";
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { }
        return "";
    }

    private static void TrimCmdlineCache(Dictionary<int, DateTime> liveStartTimes)
    {
        lock (_cmdlineLock)
        {
            if (_cmdlineCache.Count < 4096) return;
            var live = new HashSet<int>(liveStartTimes.Keys);
            var dead = _cmdlineCache.Keys.Where(k => !live.Contains(k)).ToList();
            foreach (var k in dead) _cmdlineCache.Remove(k);
        }
    }

    private DateTime SafeStartTime(ProcessThread t)
    {
        try { return t.StartTime; } catch { return DateTime.MinValue; }
    }

    /// <summary>PID -> main window title. Filters cloaked (UWP suspended shells), tool windows
    /// and untitled windows so background helper windows don't count as "user is using it".</summary>
    private Dictionary<int, string> GetVisibleWindowPids()
    {
        var map = new Dictionary<int, string>();
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                    int exStyle = NativeMethods.GetWindowLong(hWnd, -20 /*GWL_EXSTYLE*/);
                    if ((exStyle & 0x80 /*WS_EX_TOOLWINDOW*/) != 0) return true;
                    if (NativeMethods.DwmGetWindowAttribute(hWnd, 14 /*DWMWA_CLOAKED*/, out var cloaked, 4) == 0 && cloaked != 0) return true;
                    if (NativeMethods.GetWindowTextLength(hWnd) == 0) return true;
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != 0 && !map.ContainsKey((int)pid))
                    {
                        var sb = new System.Text.StringBuilder(256);
                        NativeMethods.GetWindowText(hWnd, sb, 256);
                        map[(int)pid] = sb.ToString();
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return map;
    }

    private Dictionary<int, int> GetTcpConnectionCountsByPid()
    {
        var dict = new Dictionary<int, int>();
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
                    int num = Marshal.ReadInt32(buf);
                    int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                    IntPtr rowPtr = buf + 4;
                    for (int i = 0; i < num; i++)
                    {
                        var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                        dict[(int)row.dwOwningPid] = dict.TryGetValue((int)row.dwOwningPid, out var c) ? c + 1 : 1;
                        rowPtr += rowSize;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return dict;
    }

    // ---- caches for slow external data ----

    public static List<ScheduledTaskInfo> GetScheduledTasksCached()
    {
        lock (_cacheLock)
        {
            if (_taskCache != null && (DateTime.UtcNow - _taskCache.Value.time).TotalMinutes < 10)
                return _taskCache.Value.tasks;
        }
        var tasks = new ScheduledTaskService().Enumerate();
        lock (_cacheLock) { _taskCache = (DateTime.UtcNow, tasks); }
        return tasks;
    }

    public static List<StartupItem> GetStartupItemsCached()
    {
        lock (_cacheLock)
        {
            if (_startupCache != null && (DateTime.UtcNow - _startupCache.Value.time).TotalMinutes < 5)
                return _startupCache.Value.items;
        }
        var items = new StartupService().Enumerate();
        lock (_cacheLock) { _startupCache = (DateTime.UtcNow, items); }
        return items;
    }

    // ---- action primitives ----

    public void TerminateProcess(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, pid);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess(PROCESS_TERMINATE) failed");
        try
        {
            if (!NativeMethods.TerminateProcess(h, 1)) throw new Exception("TerminateProcess failed");
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>Suspend the whole process atomically (no stale-TID races).</summary>
    public void SuspendProcess(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess(PROCESS_SUSPEND_RESUME) failed");
        try
        {
            int status = NativeMethods.NtSuspendProcess(h);
            if (status != 0) throw new Exception($"NtSuspendProcess 0x{status:X}");
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    public void ResumeProcess(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess(PROCESS_SUSPEND_RESUME) failed");
        try
        {
            int status = NativeMethods.NtResumeProcess(h);
            if (status != 0) throw new Exception($"NtResumeProcess 0x{status:X}");
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>Top-level visible windows owned by pid (used for graceful WM_CLOSE).</summary>
    public List<IntPtr> GetProcessWindowHandles(int pid)
    {
        var handles = new List<IntPtr>();
        try
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint wpid);
                    if (wpid == (uint)pid && NativeMethods.IsWindowVisible(hWnd))
                        handles.Add(hWnd);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return handles;
    }

    public void PostCloseToWindows(int pid)
    {
        foreach (var h in GetProcessWindowHandles(pid))
            NativeMethods.PostMessage(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    public void TrimWorkingSet(int pid)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var psapi = NativeLibrary.Load("psapi.dll");
                    try
                    {
                        var proc = NativeLibrary.GetExport(psapi, "K32EmptyWorkingSet");
                        var del = Marshal.GetDelegateForFunctionPointer<EmptyWsDelegate>(proc);
                        del(h);
                    }
                    finally { NativeLibrary.Free(psapi); }
                }
                catch { }
                finally { NativeMethods.CloseHandle(h); }
            }
        }
        catch { }
    }

    /// <summary>Lower memory priority (1..5, very low .. normal). Pages of this process become
    /// first candidates for repurposing — the polite version of "clean this app's memory".</summary>
    public bool SetMemoryPriority(int pid, uint priority)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var info = new NativeMethods.PROCESS_MEMORY_PRIORITY { MemoryPriority = priority };
                return NativeMethods.SetProcessInformation(h, (int)NativeMethods.PROCESS_INFORMATION_CLASS.ProcessMemoryPriority, ref info, Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_PRIORITY>());
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { return false; }
    }

    /// <summary>EcoQoS / power throttling: let the scheduler deprioritize this process instead of killing it.</summary>
    public bool SetEcoMode(int pid, bool on)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var info = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = 0x1, // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                    StateMask = on ? 0x1u : 0x0u
                };
                return NativeMethods.SetProcessInformation(h, (int)NativeMethods.PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, ref info, Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
            }
            finally { NativeMethods.CloseHandle(h); }
        }
        catch { return false; }
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
