using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemPilot.Helpers;

/// <summary>
/// 子进程托管（v0.25.0）：把子进程绑进一个 Job Object 并设置 KILL_ON_JOB_CLOSE。
///
/// 为什么必须：Windows 没有 Unix 的进程组信号。`mempilot lease run -- python gen.py` 的
/// 包装进程如果被 taskkill 或崩溃，python 会继续跑——而租约 TTL 到期会把挂起的进程全部恢复，
/// 于是任务和恢复出来的程序抢内存。Job 句柄关闭时内核会终止所有成员进程。
///
/// 降级行为：拿不到/绑不上 Job（极少数受限环境）时 <see cref="IsValid"/> / <see cref="Assign"/>
/// 返回 false，调用方必须打印"子进程不随包装进程退出"的提示，而不是默默当成功。
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, buf, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buf, (uint)len))
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
        catch
        {
            try { CloseHandle(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public bool IsValid => _handle != IntPtr.Zero;

    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero || process == null) return false;
        try { return AssignProcessToJobObject(_handle, process.Handle); }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        // 关闭句柄 = 触发 KILL_ON_JOB_CLOSE，成员进程全部终止
        try { CloseHandle(_handle); } catch { }
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
