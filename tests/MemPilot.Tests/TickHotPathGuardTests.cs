using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.22.0 热路径守卫：实测慢 tick 的 1086/1090ms 全在**枚举阶段**，其根因是
/// `schtasks.exe` 每 10 分钟同步执行（11 分钟节律）与 `LookupUnknowns` 的 3 秒同步等待。
/// 这两处都不允许再回到"在 tick 线程里同步等外部进程"的写法。
/// </summary>
public class TickHotPathGuardTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;

    private static string Core(params string[] parts) => File.ReadAllText(
        Path.Combine(new[] { RepoRoot(), "src", "MemPilot.Core" }.Concat(parts).ToArray()));

    private static string App(params string[] parts) => File.ReadAllText(
        Path.Combine(new[] { RepoRoot(), "src", "MemPilot.App" }.Concat(parts).ToArray()));

    [Fact]
    public void ScheduledTaskCache_RefreshesInBackground_InsteadOfBlockingTheTick()
    {
        var src = Core("Services", "ProcessService.cs");
        Assert.Contains("StartBackgroundTaskRefresh", src);
        Assert.Contains("Interlocked.Exchange(ref _taskRefreshRunning", src);
        // 过期时必须先返回旧数据（stale-while-revalidate），不允许"过期即同步枚举"
        Assert.Contains("stale", src);
    }

    [Fact]
    public void LookupUnknowns_NoLongerBlocksTheRefresh()
    {
        var src = App("ViewModels", "MainViewModel.Collections.cs");
        // 允许注释里提到旧写法，但不能再有实际调用
        Assert.DoesNotContain("Task.WaitAll(tasks", src);
        Assert.Contains("_lookupInFlight", src);
        Assert.Contains("Task.WhenAll", src);
    }

    [Fact]
    public void SelfCost_HasLookupSegment()
    {
        var src = Core("Services", "SelfCostRecorder.cs");
        Assert.Contains("LookupMs", src);
        Assert.Contains("t.LookupMs", src);
    }
}
