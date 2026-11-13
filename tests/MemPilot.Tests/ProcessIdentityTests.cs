using System.Diagnostics;
using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.18.0（P0）身份复检回归。
///
/// 故障背景：<c>IsStillSameProcess</c> 在 StartTime 未知（提权进程读不到）时**放行**，
/// 而挂起登记写进去的正是 <see cref="DateTime.MinValue"/>；到期恢复时 IsAliveSame 必然
/// 比对失败，ResumeRecord 随即清除记账——进程永久挂起且不再重试。
///
/// 现行语义：身份未知一律拒绝（对 L2/L3/L4）；L1 只压缩工作集，验证失败时回退按 PID 执行。
/// 动作路径改用 OpenVerified，复检与动作共用同一句柄，消除 TOCTOU。
/// </summary>
public class ProcessIdentityTests
{
    private static ProcessInfo Proc(int pid, DateTime start) => new()
    {
        Pid = pid,
        Name = "test",
        StartTime = start
    };

    [Fact]
    public void IsStillSameProcess_UnknownStartTime_IsRejected()
    {
        var p = Proc(Environment.ProcessId, DateTime.MinValue);
        Assert.False(CleanupEngine.IsStillSameProcess(p));
    }

    [Fact]
    public void IsStillSameProcess_InvalidPid_IsRejected()
    {
        Assert.False(CleanupEngine.IsStillSameProcess(Proc(0, DateTime.Now)));
        Assert.False(CleanupEngine.IsStillSameProcess(Proc(-1, DateTime.Now)));
    }

    [Fact]
    public void IsStillSameProcess_CurrentProcess_IsAccepted()
    {
        using var me = Process.GetCurrentProcess();
        Assert.True(CleanupEngine.IsStillSameProcess(Proc(me.Id, me.StartTime)));
    }

    [Fact]
    public void IsStillSameProcess_MismatchedStartTime_IsRejected()
    {
        // 模拟 PID 复用：同一 PID、不同启动时间
        using var me = Process.GetCurrentProcess();
        Assert.False(CleanupEngine.IsStillSameProcess(Proc(me.Id, me.StartTime.AddMinutes(5))));
    }

    [Fact]
    public void HandleMatchesStartTime_NullHandle_IsFalse()
    {
        Assert.False(ProcessService.HandleMatchesStartTime(IntPtr.Zero, DateTime.Now));
    }

    [Fact]
    public void HandleMatchesStartTime_MinValue_IsFalse()
    {
        using var me = Process.GetCurrentProcess();
        Assert.False(ProcessService.HandleMatchesStartTime(me.Handle, DateTime.MinValue));
    }

    [Fact]
    public void HandleMatchesStartTime_OwnHandle_MatchesOwnStartTime()
    {
        using var me = Process.GetCurrentProcess();
        Assert.True(ProcessService.HandleMatchesStartTime(me.Handle, me.StartTime));
    }

    [Fact]
    public void OpenVerified_UnknownStartTime_NeverOpens()
    {
        var svc = new ProcessService();
        var h = svc.OpenVerified(Environment.ProcessId,
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, DateTime.MinValue);
        Assert.Equal(IntPtr.Zero, h);
    }

    [Fact]
    public void OpenVerified_MismatchedStartTime_ClosesAndReturnsZero()
    {
        var svc = new ProcessService();
        using var me = Process.GetCurrentProcess();
        var h = svc.OpenVerified(me.Id, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            me.StartTime.AddMinutes(5));
        Assert.Equal(IntPtr.Zero, h);
    }

    [Fact]
    public void OpenVerified_CurrentProcess_OpensAndVerifies()
    {
        var svc = new ProcessService();
        using var me = Process.GetCurrentProcess();
        var h = svc.OpenVerified(me.Id, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, me.StartTime);
        try
        {
            Assert.NotEqual(IntPtr.Zero, h);
        }
        finally
        {
            ProcessService.CloseProcessHandle(h);
        }
    }
}
