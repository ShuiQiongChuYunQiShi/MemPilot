using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.21.0 长驻未用建议：跑满 8h、今天零活动、私有提交 ≥512MB 的非垃圾进程，
/// 只建议挂起。默认关闭，与垃圾建议互斥（每天最多一张卡）。
/// </summary>
public class LongResidentAdvisorTests : IDisposable
{
    private readonly string _dir;

    public LongResidentAdvisorTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "lr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ProcessInfo P(int pid, string name, bool window = false,
        ProcessCategory cat = ProcessCategory.ThirdPartyApp, DateTime? start = null,
        long privateBytes = 1024L * 1024 * 1024)
        => new()
        {
            Pid = pid,
            Name = name,
            Category = cat,
            HasVisibleWindow = window,
            // ProcessInfo.StartTime 来自 Process.StartTime（本地时间轴）——
            // JunkSafetyFilter 用 nowLocal 比较，两者必须同轴
            StartTime = start ?? DateTime.Now - TimeSpan.FromHours(10),
            Memory = new MemoryMetrics { PrivateBytes = privateBytes, PrivateWorkingSetBytes = privateBytes }
        };

    private static Dictionary<string, ProcessBehavior> Behaviors(
        params (string name, DateTime? lastActive)[] items)
    {
        var d = new Dictionary<string, ProcessBehavior>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lastActive) in items)
            d[name] = new ProcessBehavior { Name = name, LastActiveUtc = lastActive ?? DateTime.MinValue };
        return d;
    }

    /// <summary>昨天 23:00（本地）→ 今天零活跃。</summary>
    private static DateTime Yesterday => DateTime.Today.AddDays(-1).AddHours(23).ToUniversalTime();

    private JunkAdvice? Recommend(List<ProcessInfo> procs, Dictionary<string, ProcessBehavior> behaviors,
        IReadOnlyCollection<string>? whitelist = null, JunkAdviceStore? store = null)
        => LongResidentAdvisor.Recommend(procs, behaviors, -1, new HashSet<int>(),
            whitelist ?? Array.Empty<string>(), store, DateTime.UtcNow);

    [Fact]
    public void PicksLongRunningUnusedLargeProcess()
    {
        var advice = Recommend(new List<ProcessInfo> { P(1, "ArmouryCrate.Service") },
            Behaviors(("ArmouryCrate.Service", Yesterday)));

        Assert.NotNull(advice);
        Assert.Equal(JunkAdviceKind.LongResident, advice!.Kind);
        Assert.Equal("ArmouryCrate.Service", advice.Candidates[0].Name);
        Assert.Contains("长驻未用", advice.Reason);
    }

    [Fact]
    public void SkipsYoungProcess()
    {
        var advice = Recommend(
            new List<ProcessInfo> { P(1, "fresh", start: DateTime.Now - TimeSpan.FromHours(1)) },
            Behaviors(("fresh", Yesterday)));
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsSmallProcess()
    {
        var advice = Recommend(
            new List<ProcessInfo> { P(1, "tiny", privateBytes: 100L * 1024 * 1024) },
            Behaviors(("tiny", Yesterday)));
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsProcessUsedToday()
    {
        var advice = Recommend(new List<ProcessInfo> { P(1, "used") },
            Behaviors(("used", DateTime.UtcNow - TimeSpan.FromMinutes(30))));
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsProcessWithVisibleWindow()
    {
        var advice = Recommend(new List<ProcessInfo> { P(1, "windowed", window: true) },
            Behaviors(("windowed", Yesterday)));
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsWhitelisted()
    {
        var advice = Recommend(new List<ProcessInfo> { P(1, "keepme") },
            Behaviors(("keepme", Yesterday)), whitelist: new[] { "keepme" });
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsWhenBehaviorLedgerHasNoEntry()
    {
        // 没有账本 = 无法证明"今天没活动"，不猜（刚启动的进程会被误报）
        var advice = Recommend(new List<ProcessInfo> { P(1, "unknown-history") },
            new Dictionary<string, ProcessBehavior>());
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsVmMemoryHost()
    {
        // vmmem 是 WSL/Hyper-V 的内存宿主，挂起会冻结整个子系统
        var advice = Recommend(new List<ProcessInfo> { P(1, "vmmemWSL") },
            Behaviors(("vmmemWSL", Yesterday)));
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsBloatwareAndProtectedCategories()
    {
        // 垃圾名单归 JunkAdvisor；Unknown/系统关键等被安全过滤排除
        var advice = Recommend(
            new List<ProcessInfo>
            {
                P(1, "wps", cat: ProcessCategory.Bloatware),
                P(2, "mystery", cat: ProcessCategory.Unknown),
            },
            Behaviors(("wps", Yesterday), ("mystery", Yesterday)));
        Assert.Null(advice);
    }

    [Fact]
    public void SkipsDuringLearningCooldown()
    {
        var store = new JunkAdviceStore(_dir);
        store.RecordDismissed("mspc", DateTime.UtcNow);   // 忽略过 → 7 天不再提
        var advice = Recommend(new List<ProcessInfo> { P(1, "mspc") },
            Behaviors(("mspc", Yesterday)), store: store);
        Assert.Null(advice);
    }

    [Fact]
    public void DeduplicatesSameNameAndCapsAtThree()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "dup"), P(2, "dup"),
            P(3, "a"), P(4, "b"), P(5, "c"),
        };
        var advice = Recommend(procs,
            Behaviors(("dup", Yesterday), ("a", Yesterday), ("b", Yesterday), ("c", Yesterday)));

        Assert.NotNull(advice);
        Assert.Equal(LongResidentAdvisor.MaxCandidates, advice!.Candidates.Count);
        Assert.Single(advice.Candidates.Where(p => p.Name == "dup"));
    }

    [Fact]
    public void IsSuspendUnsafe_MatchesVmMemory()
    {
        Assert.True(LongResidentAdvisor.IsSuspendUnsafe("vmmem"));
        Assert.True(LongResidentAdvisor.IsSuspendUnsafe("vmmemWSL"));
        Assert.False(LongResidentAdvisor.IsSuspendUnsafe("ArmouryCrate"));
    }

    [Fact]
    public void Store_LongResidentDayKey_PersistsAcrossRestart()
    {
        var store = new JunkAdviceStore(_dir);
        Assert.Equal("", store.LongResidentDayKey);
        store.MarkLongResidentDelivered("20261123");
        Assert.Equal("20261123", new JunkAdviceStore(_dir).LongResidentDayKey);

        store.ClearLongResidentDay();
        Assert.Equal("", new JunkAdviceStore(_dir).LongResidentDayKey);
    }

    [Fact]
    public void DailyGate_IsBlockedWhenAnotherAdviceWasDeliveredToday()
    {
        var evening = new DateTime(2026, 11, 23, 20, 0, 0);
        Assert.False(ResidentEngine.ShouldEvaluateJunkAdvice(evening, 19, null,
            default, uiActive: true, anotherAdviceDeliveredToday: true));
        Assert.True(ResidentEngine.ShouldEvaluateJunkAdvice(evening, 19, null,
            default, uiActive: true, anotherAdviceDeliveredToday: false));
    }

    [Fact]
    public void Store_DayKeysAreIndependent()
    {
        var store = new JunkAdviceStore(_dir);
        store.MarkAdviceDelivered("20261123");
        Assert.Equal("", store.LongResidentDayKey);
        store.MarkLongResidentDelivered("20261123");
        Assert.Equal("20261123", store.AdviceDayKey);
        Assert.Equal("20261123", store.LongResidentDayKey);
    }
}
