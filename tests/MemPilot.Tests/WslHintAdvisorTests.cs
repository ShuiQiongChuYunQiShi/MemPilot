using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.22.0 WSL 常驻提示：只匹配 WSL 的 vmmem（裸 vmmem 是 Hyper-V，shutdown 无效），
/// 占用按 Max(commit, WS) 取，且要求已运行够久。
/// </summary>
public class WslHintAdvisorTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestTmp.Dir, "wsl-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const long Gb = 1024L * 1024 * 1024;

    private static ProcessInfo Vmmem(string name = "vmmemWSL", long priv = 4 * Gb, double hours = 10)
        => new()
        {
            Pid = 4242,
            Name = name,
            Category = ProcessCategory.ContainerVm,
            StartTime = DateTime.Now - TimeSpan.FromHours(hours),
            Memory = new MemoryMetrics { PrivateBytes = priv, PrivateWorkingSetBytes = priv / 2 }
        };

    [Fact]
    public void PicksWslMemoryHostAboveThreshold()
    {
        var hint = WslHintAdvisor.Recommend(new List<ProcessInfo> { Vmmem() }, 2 * Gb, 60, DateTime.Now);

        Assert.NotNull(hint);
        Assert.Equal(10, hint!.RunningHours);
        Assert.Equal("vmmemWSL", hint.Process.Name);
        Assert.True(hint.TotalGb > 3.9);
    }

    [Fact]
    public void IgnoresNonWslVmmem()
    {
        // 裸 vmmem 通常是 Hyper-V/Windows Sandbox：wsl shutdown 无效，不该提示
        Assert.Null(WslHintAdvisor.Recommend(new List<ProcessInfo> { Vmmem("vmmem") }, 1 * Gb, 0, DateTime.Now));
    }

    [Fact]
    public void IgnoresBelowSizeThreshold()
        => Assert.Null(WslHintAdvisor.Recommend(new List<ProcessInfo> { Vmmem(priv: 1 * Gb) }, 2 * Gb, 60, DateTime.Now));

    [Fact]
    public void IgnoresRecentlyStarted()
        => Assert.Null(WslHintAdvisor.Recommend(new List<ProcessInfo> { Vmmem(hours: 0.1) }, 2 * Gb, 60, DateTime.Now));

    [Fact]
    public void FallsBackToWorkingSetWhenCommitIsSmaller()
    {
        // vmmem 的 WS 会被系统回收，单看 commit 会漏报
        var p = Vmmem(priv: 0);
        p.Memory.PrivateWorkingSetBytes = 3 * Gb;
        Assert.NotNull(WslHintAdvisor.Recommend(new List<ProcessInfo> { p }, 2 * Gb, 60, DateTime.Now));
    }

    [Fact]
    public void IgnoresMissingProcess()
        => Assert.Null(WslHintAdvisor.Recommend(new List<ProcessInfo>(), 2 * Gb, 60, DateTime.Now));

    [Fact]
    public void Store_WslHintDayKey_PersistsAcrossRestart()
    {
        var store = new JunkAdviceStore(_dir);
        Assert.Equal("", store.WslHintDayKey);

        store.MarkWslHintDelivered("20261123");
        Assert.Equal("20261123", new JunkAdviceStore(_dir).WslHintDayKey);

        store.ClearWslHintDay();
        Assert.Equal("", new JunkAdviceStore(_dir).WslHintDayKey);
    }

    [Fact]
    public void Store_WslHintKeyIsIndependentFromOtherAdviceKeys()
    {
        var store = new JunkAdviceStore(_dir);
        store.MarkWslHintDelivered("20261123");
        Assert.Equal("", store.AdviceDayKey);
        Assert.Equal("", store.LongResidentDayKey);
    }
}
