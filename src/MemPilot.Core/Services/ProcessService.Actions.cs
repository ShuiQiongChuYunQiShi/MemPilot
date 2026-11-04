using System.Diagnostics;
using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 处置原语（v0.9.2 从 ProcessService.cs 拆出）：终止 / 挂起 / 恢复 / 工作集压缩 /
/// 内存优先级 / EcoQoS / 线程级挂起恢复。全部是"直接对进程动手"的操作，
/// 与采集逻辑分开以便一眼看清「哪些代码会改变系统状态」。
/// 安全筛选不在这里——调用方必须先过 TrimSelection / 各入口的守卫。
/// </summary>
public partial class ProcessService
{
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
                    var psapi = NativeLibrary.Load("kernel32.dll"); // K32* exports live here, not in psapi
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
}
