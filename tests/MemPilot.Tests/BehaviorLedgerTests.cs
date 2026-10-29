using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

public class BehaviorLedgerTests : IDisposable
{
    private readonly string _dir;

    public BehaviorLedgerTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-behavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ProcessInfo P(string name, bool window = false, int idleMin = 0, bool audio = false, long ws = 200L * 1024 * 1024)
        => new()
        {
            Pid = 1, Name = name, HasVisibleWindow = window, AudioActive = audio,
            IdleMinutes = idleMin,
            Memory = new MemoryMetrics { PrivateWorkingSetBytes = ws }
        };

    [Fact]
    public void Observe_AccumulatesRatios_AndScoresHeadlessIdleHigh()
    {
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        for (int i = 0; i < 10; i++)
            ledger.Observe(new List<ProcessInfo> { P("junkhelper", window: false, idleMin: 5), P("editor", window: true) });

        var junk = ledger.JunkScore("junkhelper");
        var editor = ledger.JunkScore("editor");
        Assert.True(junk > 7, $"headless idle score too low: {junk}");
        Assert.True(editor <= 3.5, $"windowed active score too high: {editor}");
    }

    [Fact]
    public void OrphanObservation_AddsBonus()
    {
        // 用有窗口场景避免分数饱和（无窗口+空闲+全在场会顶到 10 分封顶）
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        for (int i = 0; i < 5; i++)
            ledger.Observe(new List<ProcessInfo> { P("QQLiveService", window: true) });
        var withOrphan = ledger.JunkScore("QQLiveService");

        var ledger2 = BehaviorLedger.Load(Path.Combine(_dir, "behavior2.json"));
        for (int i = 0; i < 5; i++)
            ledger2.Observe(new List<ProcessInfo> { P("QQLiveService", window: true), P("QQLive") }); // 宿主在场
        var noOrphan = ledger2.JunkScore("QQLiveService");

        Assert.True(withOrphan > noOrphan, $"orphan bonus missing: {withOrphan} vs {noOrphan}");
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(_dir, "behavior.json");
        var ledger = BehaviorLedger.Load(path);
        for (int i = 0; i < 3; i++) ledger.Observe(new List<ProcessInfo> { P("bigapp") });
        ledger.Save();

        var reloaded = BehaviorLedger.Load(path);
        Assert.True(reloaded.Entries.ContainsKey("bigapp"));
        Assert.Equal(3, reloaded.Entries["bigapp"].Observations);
        Assert.Equal(ledger.JunkScore("bigapp"), reloaded.JunkScore("bigapp"));
    }

    [Fact]
    public void UnknownProcess_ScoreIsZero()
    {
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        ledger.Observe(new List<ProcessInfo> { P("something") });
        Assert.Equal(0, ledger.JunkScore("never-seen"));
    }
}
