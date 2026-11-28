using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 压缩收益历史（v0.26.0：延迟回弹测量）。
///
/// 为什么不能测"即时释放率"：`EmptyWorkingSet` 几乎总能把工作集压到接近 0，
/// 于是每个进程都是 1.0（2026-11-28 实测 24 条全在 0.989–1.0），指标没有区分度。
/// 真正有区别的是**压完之后多久长回来**。
/// </summary>
public class TrimFeedbackStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(TestTmp.Dir, "trim-fb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static List<ProcessInfo> SnapshotOf(params (int pid, DateTime start)[] items)
        => items.Select(x => new ProcessInfo { Pid = x.pid, StartTime = x.start }).ToList();

    [Fact]
    public void UnknownName_UsesDefaultRatio()
    {
        var store = new TrimFeedbackStore(_dir);
        Assert.Equal(TrimFeedbackStore.DefaultRatio, store.RatioOf("never-seen"));
    }

    [Fact]
    public void SettleDue_ComputesEffectiveFromRebound()
    {
        var store = new TrimFeedbackStore(_dir);
        var procs = SnapshotOf((42, Now));
        // 压前 1000、压后 100（释放 900）；60 秒后回到 640 → 回弹 540 → effective = 1 - 540/900 = 0.4
        for (int i = 0; i < 2; i++)
        {
            store.BeginObservation("p", 42, Now, wsBefore: 1000, wsAfter: 100, nowUtc: Now);
            Assert.Equal(1, store.SettleDue(procs, Now.AddSeconds(61), _ => 640));
        }
        Assert.Equal(0.4, store.RatioOf("p"), precision: 3);
    }

    [Fact]
    public void FullRebound_IsZeroEffective_AndStaysPut_IsOne()
    {
        var full = new TrimFeedbackStore(Path.Combine(_dir, "a"));
        var stay = new TrimFeedbackStore(Path.Combine(_dir, "b"));
        var procs = SnapshotOf((1, Now));

        for (int i = 0; i < 2; i++)
        {
            full.BeginObservation("bouncer", 1, Now, 1000, 100, Now);
            full.SettleDue(procs, Now.AddSeconds(61), _ => 1000);   // 完全回弹
        }
        for (int i = 0; i < 2; i++)
        {
            stay.BeginObservation("sleeper", 1, Now, 1000, 100, Now);
            stay.SettleDue(procs, Now.AddSeconds(61), _ => 100);    // 一直没长回来
        }

        Assert.Equal(0, full.RatioOf("bouncer"), precision: 3);
        Assert.Equal(1, stay.RatioOf("sleeper"), precision: 3);
    }

    [Fact]
    public void SettleDue_DropsExitedProcess()
    {
        var store = new TrimFeedbackStore(_dir);
        store.BeginObservation("gone", 7, Now, 1000, 100, Now);

        Assert.Equal(0, store.SettleDue(new List<ProcessInfo>(), Now.AddSeconds(61), _ => 200));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void SettleDue_DropsReusedPid()
    {
        var store = new TrimFeedbackStore(_dir);
        store.BeginObservation("x", 7, Now, 1000, 100, Now);
        // 同一 PID，但启动时间对不上 = 已被复用
        var procs = SnapshotOf((7, Now.AddMinutes(5)));

        Assert.Equal(0, store.SettleDue(procs, Now.AddSeconds(61), _ => 200));
    }

    [Fact]
    public void SettleDue_NotYetDue_KeepsPending()
    {
        var store = new TrimFeedbackStore(_dir);
        store.BeginObservation("y", 9, Now, 1000, 100, Now);

        Assert.Equal(0, store.SettleDue(SnapshotOf((9, Now)), Now.AddSeconds(30), _ => 200));
        Assert.Equal(1, store.PendingCount);
    }

    [Fact]
    public void SingleSample_StillUsesDefault_SoOneOddRunCannotFlipTheOrder()
    {
        var store = new TrimFeedbackStore(_dir);
        store.BeginObservation("once", 3, Now, 1000, 100, Now);
        store.SettleDue(SnapshotOf((3, Now)), Now.AddSeconds(61), _ => 100);

        Assert.Equal(TrimFeedbackStore.DefaultRatio, store.RatioOf("once"));
    }

    [Fact]
    public void PersistsAcrossReload()
    {
        var store = new TrimFeedbackStore(_dir);
        for (int i = 0; i < 2; i++)
        {
            store.BeginObservation("r", 5, Now, 1000, 100, Now);
            store.SettleDue(SnapshotOf((5, Now)), Now.AddSeconds(61), _ => 460);  // effective 0.6
        }
        store.Flush();

        var reloaded = new TrimFeedbackStore(_dir);
        Assert.Equal(0.6, reloaded.RatioOf("r"), precision: 3);
    }

    [Fact]
    public void InvalidObservation_IsIgnored()
    {
        var store = new TrimFeedbackStore(_dir);
        store.BeginObservation("", 1, Now, 1000, 100, Now);        // 无名字
        store.BeginObservation("s", 1, Now, 0, 100, Now);          // 压前未知
        store.BeginObservation("t", 1, Now, 500, 600, Now);        // 压后反而更大

        Assert.Equal(0, store.PendingCount);
    }
}
