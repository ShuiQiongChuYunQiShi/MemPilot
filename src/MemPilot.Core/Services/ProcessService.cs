using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 进程采集与动作原语。
///
/// v0.9.2（三轮复盘 P2）：按职责拆成 partial —— 本文件保留**采集**（枚举/内存/IO/命令行走/窗口/缓存），
/// <c>ProcessService.Actions.cs</c> 放**处置原语**（终止/挂起/恢复/压缩/QoS/线程）。
/// 拆成 partial 而非新类的理由：两者共享同一个进程句柄语义与 TrustVerifier，且 40+ 处调用点
/// 都写作 `_vm.ProcessService.Xxx(...)`；换类名要改一圈调用点，收益不足以抵消回归风险。
/// </summary>
public partial class ProcessService
{
    private readonly TrustVerifier _trust = new();
    private static DateTime _lastEnumDiagUtc = DateTime.MinValue;

    // ---- caches: scheduled tasks & startup items rarely change; do not spawn schtasks every refresh ----
    private static (DateTime time, List<ScheduledTaskInfo> tasks)? _taskCache;
    private static (DateTime time, List<StartupItem> items)? _startupCache;
    private static readonly object _cacheLock = new();

    // file version metadata cache: GetVersionInfo reads the PE header from disk on every call
    private static void FillVersionInfo(ProcessInfo info)
    {
        var path = info.ExecutablePath;
        if (string.IsNullOrEmpty(path)) return;
        var meta = MemPilot.Helpers.ExeMetadataCache.Read(path);
        info.Company = meta.Company;
        info.ProductName = meta.Product;
        info.FileDescription = meta.Description;
        info.FileVersion = meta.Version;
    }

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
        int restricted = 0; // v0.3.9：本轮读取受限的进程数（诊断用）
        var startupItems = GetStartupItemsCached();
        var scheduledTasks = GetScheduledTasksCached();
        var procs = Process.GetProcesses();
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
                try { info.StartTime = p.StartTime; } catch { info.StartTime = DateTime.MinValue; info.AccessDenied = true; }
                try { info.Memory.HandleCount = p.HandleCount; } catch { }

                // one handle serves path / parent / command line / memory / IO
                CollectDetail(p.Id, p, info);

                // visible window + title
                if (windowPids.TryGetValue(p.Id, out var title))
                {
                    info.HasVisibleWindow = true;
                    info.MainWindowTitle = title;
                }
                else if (_windowEnumDegraded)
                {
                    // v0.9.1：窗口枚举失败时"没查到窗口"不可信——按"可能在使用"处理，
                    // 避免进程被当成无窗口而进入 L4/自动清扫/硬租约挂起候选。
                    info.HasVisibleWindow = true;
                    info.WindowEnumDegraded = true;
                }
                try { if (string.IsNullOrEmpty(info.MainWindowTitle)) info.MainWindowTitle = p.MainWindowTitle ?? ""; } catch { }

                info.IsStartupItem = StartupService.IsStartupPath(info.ExecutablePath, startupItems);
                info.IsScheduledTask = ScheduledTaskService.IsScheduledTaskProcess(info.ExecutablePath, scheduledTasks);

                // file metadata (cached) + trust (cached by path)
                if (!string.IsNullOrEmpty(info.ExecutablePath) && File.Exists(info.ExecutablePath))
                {
                    FillVersionInfo(info);
                    var (verified, subject, issuer) = _trust.Verify(info.ExecutablePath);
                    info.SignatureVerified = verified;
                    info.IsSigned = verified;
                    info.SignatureSubject = subject;
                    info.SignatureIssuer = issuer;
                }

                if (tcpCounts.TryGetValue(p.Id, out var tcpCount)) info.Memory.NetworkConnections = tcpCount;
                try { info.Memory.TotalCpuTime = p.TotalProcessorTime; } catch { }

                // thread COUNT only — the per-thread listing is expensive (one syscall per
                // thread for start time) and needed only on the thread tab, on demand
                try { info.Memory.ThreadCount = p.Threads.Count; } catch { }
                info.Memory.LastActivityUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                restricted++;
                info.AccessDenied = true;
                info.AccessNote = ex.Message;
            }
            result.Add(info);
        }

        // v0.3.9：受限进程过多说明数据通路有问题（提权失败/安全软件拦截），
        // 但每轮都记会把诊断刷满，10 分钟最多记一次。
        if (restricted > 0 && (DateTime.UtcNow - _lastEnumDiagUtc).TotalMinutes >= 10)
        {
            _lastEnumDiagUtc = DateTime.UtcNow;
            Diag.Record("proc-enum", $"{restricted}/{result.Count} 个进程读取受限（提权/安全软件？）");
        }

        // child lists from the parent PIDs we just collected
        var byPid = new Dictionary<int, ProcessInfo>(result.Count);
        foreach (var info in result) byPid[info.Pid] = info;
        foreach (var info in result)
        {
            if (info.ParentPid != 0 && byPid.TryGetValue(info.ParentPid, out var parent) && parent != info)
                parent.ChildPids.Add(info.Pid);
        }

        TrimCmdlineCache(startTimes);
        return result;
    }

    /// <summary>One PROCESS_QUERY_LIMITED_INFORMATION handle serves every query we need.</summary>
    private void CollectDetail(int pid, Process p, ProcessInfo info)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) { info.AccessDenied = true; return; }
        try
        {
            info.ExecutablePath = NativeMethods.GetProcessImageNameWin32(h);
            info.ParentPid = QueryParentPid(h);
            info.CommandLine = GetCommandLineCached(pid, h);
            CollectMemory(h, p, info);
            CollectIo(h, info);
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>On-demand full thread listing (thread tab only) — one syscall per thread.</summary>
    public List<ThreadInfo> EnumerateAllThreads(IEnumerable<ProcessInfo> procs)
    {
        var list = new List<ThreadInfo>();
        foreach (var p in procs)
        {
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                proc.Refresh();
                foreach (ProcessThread t in proc.Threads)
                {
                    list.Add(new ThreadInfo
                    {
                        Tid = t.Id,
                        OwnerPid = p.Pid,
                        State = t.ThreadState,
                        WaitReason = t.WaitReason,
                        Priority = t.BasePriority,
                        TotalCpuTime = t.TotalProcessorTime,
                        StartTime = SafeStartTime(t)
                    });
                }
            }
            catch { }
        }
        return list;
    }

    private void CollectMemory(IntPtr hProcess, Process p, ProcessInfo info)
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
            var counters = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX2();
            counters.cb = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX2>();
            if (NativeMethods.K32GetProcessMemoryInfo(hProcess, ref counters, counters.cb))
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
        catch { }
    }

    private void CollectIo(IntPtr hProcess, ProcessInfo info)
    {
        try
        {
            if (NativeMethods.GetProcessIoCounters(hProcess, out var io))
            {
                info.Memory.IoReadBytes = (long)io.ReadTransferCount;
                info.Memory.IoWriteBytes = (long)io.WriteTransferCount;
                info.Memory.IoReadOps = (long)io.ReadOperationCount;
                info.Memory.IoWriteOps = (long)io.WriteOperationCount;
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

    private static int QueryParentPid(IntPtr hProcess)
    {
        try
        {
            var pbi = new NativeMethods.PROCESS_BASIC_INFORMATION();
            int status = NativeMethods.NtQueryInformationProcess(hProcess, (int)NativeMethods.PROCESSINFOCLASS.ProcessBasicInformation, ref pbi, Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _);
            if (status == 0) return pbi.InheritedFromUniqueProcessId.ToInt32();
        }
        catch { }
        return 0;
    }

    private static string GetCommandLineCached(int pid, IntPtr hProcess)
    {
        lock (_cmdlineLock)
        {
            if (_cmdlineCache.TryGetValue(pid, out var cached)) return cached;
        }
        string value = QueryCommandLine(hProcess);
        lock (_cmdlineLock)
        {
            _cmdlineCache[pid] = value;
        }
        return value;
    }

    private static string QueryCommandLine(IntPtr hProcess)
    {
        try
        {
            {
                var h = hProcess;
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
        }
        catch { }
        return "";
    }

    private static void TrimCmdlineCache(Dictionary<int, DateTime> liveStartTimes)
    {
        lock (_cmdlineLock)
        {
            if (_cmdlineCache.Count < 512) return;   // v0.14：4096→512（500 进程足够，更早回收死 PID 的命令行字符串）
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
        int windowFailures = 0;
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
                catch { windowFailures++; }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // v0.9.1（复盘 P1）：EnumWindows 整体失败时旧实现返回空 map，
            // 等于"所有进程都没窗口"——而"无窗口"正是 L4/自动清扫/硬租约挂起的准入条件。
            // 标记降级，让调用方按"无法判断"处理（fail-safe：不当作无窗口）。
            Helpers.Diag.Record("proc:windows", ex);
            _windowEnumDegraded = true;
            return map;
        }
        if (windowFailures > 0)
            Helpers.Diag.Record("proc:windows", $"{windowFailures} 个窗口读取失败（其余正常）");
        return map;
    }

    /// <summary>窗口枚举是否降级（此时"无窗口"不可信）。</summary>
    private static bool _windowEnumDegraded;

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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool EmptyWsDelegate(IntPtr hProcess);
}
