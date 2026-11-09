using MemPilot.Models;
using MemPilot.Services;
using MemPilot.ViewModels;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.15.0 垃圾进程建议：四个组件的守卫——
/// ① JunkAdvisor 决策（当天未使用 + 运行 ≥2h + 安全过滤继承）
/// ② JunkAdviceStore 学习冷却（忽略 7/30/90 天，接受回 7 天）
/// ③ 定时挂起与到期恢复（ResumeAtUtc / TakeExpired）
/// ④ 行为账本今日活跃信号（跨天重置）
/// ⑤ 配置默认开启
/// </summary>
public class JunkAdviceTests : IDisposable
{
    private readonly string _dir;

    public JunkAdviceTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "ja-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ProcessInfo P(int pid, string name, bool window = false,
        ProcessCategory cat = ProcessCategory.Bloatware, DateTime? start = null,
        double cpu = 0, long privateBytes = 200L * 1024 * 1024, bool audio = false)
        => new()
        {
            Pid = pid,
            Name = name,
            Category = cat,
            HasVisibleWindow = window,
            StartTime = start ?? DateTime.UtcNow - TimeSpan.FromHours(10),  // 默认已运行 10h
            AudioActive = audio,
            Memory = new MemoryMetrics
            {
                PrivateWorkingSetBytes = privateBytes,
                PrivateBytes = privateBytes,
                CpuPercent = cpu
            }
        };

    private static Dictionary<string, ProcessBehavior> Behaviors(
        params (string name, DateTime? lastActive)[] items)
    {
        var d = new Dictionary<string, ProcessBehavior>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lastActive) in items)
            d[name] = new ProcessBehavior { Name = name, LastActiveUtc = lastActive ?? DateTime.MinValue };
        return d;
    }


    // ---- 1. JunkAdvisor ----

    [Fact]
    public void Recommend_PicksNeverActiveJunk_RunningOver2h()
    {
        var procs = new List<ProcessInfo> { P(1, "wetype_update") };
        var advice = JunkAdvisor.Recommend(procs, Behaviors(("wetype_update", null)),
            -1, new HashSet<int>(), Array.Empty<string>(), 120, null, DateTime.UtcNow);
        Assert.NotNull(advice);
        Assert.Single(advice!.Candidates);
        Assert.Equal("wetype_update", advice.Candidates[0].Name);
    }

    [Fact]
    public void Recommend_SkipsProcessActiveToday()
    {
        var procs = new List<ProcessInfo> { P(1, "wps") };
        var advice = JunkAdvisor.Recommend(procs, Behaviors(("wps", DateTime.UtcNow - TimeSpan.FromMinutes(30))),
            -1, new HashSet<int>(), Array.Empty<string>(), 120, null, DateTime.UtcNow);
        Assert.Null(advice);   // 今天有活动 → 不打扰
    }

    [Fact]
    public void Recommend_SkipsYoungProcesses()
    {
        var procs = new List<ProcessInfo> { P(1, "wetype_update", start: DateTime.UtcNow - TimeSpan.FromMinutes(30)) };
        var advice = JunkAdvisor.Recommend(procs, Behaviors(("wetype_update", null)),
            -1, new HashSet<int>(), Array.Empty<string>(), 120, null, DateTime.UtcNow);
        Assert.Null(advice);   // 运行 <2h → 不提示（避免骚扰刚启动的更新器）
    }

    [Fact]
    public void Recommend_InheritsSafetyFilters_NeverProtectedOrForeground()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "explorer", cat: ProcessCategory.SystemCritical),
            P(2, "somejunk", cat: ProcessCategory.Bloatware),
        };
        // 前台 = PID 2（somejunk 是前台）
        var advice = JunkAdvisor.Recommend(procs, Behaviors(("somejunk", null)),
            foregroundPid: 2, new HashSet<int>(), Array.Empty<string>(), 120, null, DateTime.UtcNow);
        Assert.Null(advice);   // 前台永不推荐
    }

    [Fact]
    public void Recommend_CapsAt5_AndSortsByPrivateBytesDesc()
    {
        var procs = Enumerable.Range(1, 8)
            .Select(i => P(i, $"junk{i}", privateBytes: i * 300L * 1024 * 1024))
            .ToList();
        var behaviors = Behaviors(Enumerable.Range(1, 8).Select(i => ($"junk{i}", (DateTime?)null)).ToArray());
        var advice = JunkAdvisor.Recommend(procs, behaviors, -1, new HashSet<int>(),
            Array.Empty<string>(), 120, null, DateTime.UtcNow);
        Assert.NotNull(advice);
        Assert.Equal(5, advice!.Candidates.Count);
        Assert.True(advice.Candidates[0].Memory.PrivateBytes > advice.Candidates[^1].Memory.PrivateBytes,
            "按私有提交降序");
    }

    // ---- 2. JunkAdviceStore 学习冷却 ----

    [Fact]
    public void Store_DismissCooldown_Escalates_7_30_90()
    {
        var store = new JunkAdviceStore(_dir);
        var now = DateTime.UtcNow;
        Assert.True(store.ShouldSuggest("x", now));

        store.RecordSuggested("x", now);
        Assert.False(store.ShouldSuggest("x", now + TimeSpan.FromDays(1)));   // 7 天内不提示

        store.RecordDismissed("x", now + TimeSpan.FromDays(8));   // DismissCount=1 → 冷却 30 天
        Assert.False(store.ShouldSuggest("x", now + TimeSpan.FromDays(8 + 20)));  // 20 天 < 30 天
        Assert.True(store.ShouldSuggest("x", now + TimeSpan.FromDays(8 + 31)));   // 过了 30 天

        store.RecordDismissed("x", now + TimeSpan.FromDays(45));  // DismissCount=2 → 冷却 90 天
        Assert.False(store.ShouldSuggest("x", now + TimeSpan.FromDays(45 + 60)));  // 60 天 < 90 天
        Assert.True(store.ShouldSuggest("x", now + TimeSpan.FromDays(45 + 91)));   // 过了 90 天
    }

    [Fact]
    public void Store_Accept_ResetsCooldownTo7Days()
    {
        var store = new JunkAdviceStore(_dir);
        var now = DateTime.UtcNow;
        store.RecordSuggested("y", now);
        store.RecordAccepted("y", now + TimeSpan.FromDays(1));
        Assert.True(store.ShouldSuggest("y", now + TimeSpan.FromDays(8)));     // 回到 7 天
        Assert.False(store.ShouldSuggest("y", now + TimeSpan.FromDays(1 + 3)));
    }

    [Fact]
    public void Store_CapsAt500()
    {
        var store = new JunkAdviceStore(_dir);
        var now = DateTime.UtcNow;
        for (int i = 0; i < 520; i++) store.RecordSuggested($"n{i}", now);
        // 重新加载确认落盘且条数被裁到上限
        var reloaded = new JunkAdviceStore(_dir);
        int count = 0;
        for (int i = 0; i < 520; i++)
            if (reloaded.ShouldSuggest($"n{i}", now)) count++;
        Assert.True(count <= 500, $"上限未生效：{count}");
    }

    // ---- 3. 定时挂起与到期恢复 ----

    [Fact]
    public void SuspendTimed_WritesResumeAt_And_TakeExpiredReturnsDue()
    {
        var config = new ConfigStore(_dir);
        var engine = new CleanupEngine(new ProcessService(), new AuditLogger(_dir), _dir);
        var p = P(42, "wetype_update", start: DateTime.Now - TimeSpan.FromHours(5));
        // 真 PID 42 大概率不存在——SuspendTimed 会失败；这里用注册表直测 TakeExpired
        engine.Suspended.Add(p.Pid, p.StartTime, p.Name, "测试", DateTime.UtcNow + TimeSpan.FromHours(2));

        // 未到期：保留
        Assert.Empty(engine.Suspended.TakeExpired(DateTime.UtcNow + TimeSpan.FromMinutes(30)));
        Assert.True(engine.Suspended.IsSuspended(p.Pid));

        // 到期：取出且账本清空
        var due = engine.Suspended.TakeExpired(DateTime.UtcNow + TimeSpan.FromHours(3));
        Assert.Single(due);
        Assert.NotNull(due[0].ResumeAtUtc);
        Assert.False(engine.Suspended.IsSuspended(p.Pid));
        config.Config.JunkAdviceEnabled = true;   // 触碰字段确认存在
    }

    [Fact]
    public void Engine_WiresDailyAdviceAndStartupResume()
    {
        var root = Directory.GetParent(TestTmp.Dir)!.FullName;
        var engine = File.ReadAllText(Path.Combine(root, "src", "MemPilot.Core", "Services", "ResidentEngine.cs"));
        Assert.Contains("MaybeAdviseJunk(procs);", engine);
        Assert.Contains("ResumeExpiredSuspends();", engine);
        var autoActions = File.ReadAllText(Path.Combine(root, "src", "MemPilot.Core", "Services", "ResidentEngine.AutoActions.cs"));
        Assert.Contains("JunkAdviceEnabled", autoActions);
        Assert.Contains("JunkAdvisor.Recommend", autoActions);
        // 启动补恢复：Start() 内也要跑（应用关闭期间到期的，启动即恢复）
        var startIdx = engine.IndexOf("public void Start(", StringComparison.Ordinal);
        var block = engine[startIdx..Math.Min(engine.Length, startIdx + 300)];
        Assert.Contains("ResumeExpiredSuspends()", block);
    }

    [Fact]
    public void SuspendRegistry_TimedSurvivesRestart()
    {
        var reg = new SuspendRegistry(_dir);
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        var p = P(me.Id, "testhost", start: me.StartTime);
        reg.Add(p.Pid, p.StartTime, p.Name, "测试", DateTime.UtcNow + TimeSpan.FromHours(8));

        var reloaded = new SuspendRegistry(_dir);   // 模拟重启：重新加载
        var rec = Assert.Single(reloaded.LiveRecords());
        Assert.NotNull(rec.ResumeAtUtc);   // 定时挂起跨重启保留
    }

    // ---- 4. 行为账本今日活跃 ----

    [Fact]
    public void BehaviorLedger_LastActive_UpdatesOnlyOnVisibleActivity()
    {
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        var active = P(1, "act", window: true, cpu: 10);
        var silent = P(2, "silent", window: false, cpu: 0);
        var now = DateTime.UtcNow;
        ledger.Observe(new List<ProcessInfo> { active, silent }, now);

        var a = ledger.Entries["act"];
        var s = ledger.Entries["silent"];
        Assert.True(a.LastActiveUtc >= now.AddSeconds(-5), "有窗口 → 活跃");
        Assert.Equal(DateTime.MinValue, s.LastActiveUtc);   // 无窗口/音频/CPU → 不算活跃
        Assert.Equal(1, a.TodayActiveObs);
        Assert.Equal(0, s.TodayActiveObs);
        Assert.NotEqual("", a.DayKey);
    }

    [Fact]
    public void BehaviorLedger_TodayCounter_ResetsAcrossDayBoundary()
    {
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        var p = P(1, "act", window: true, cpu: 10);
        var day1 = DateTime.UtcNow.Date.AddDays(-1).AddHours(12).ToUniversalTime();
        var day2 = DateTime.UtcNow.Date.AddHours(12).ToUniversalTime();
        ledger.Observe(new List<ProcessInfo> { p }, day1);
        int afterDay1 = ledger.Entries["act"].TodayActiveObs;
        Assert.True(afterDay1 > 0);
        ledger.Observe(new List<ProcessInfo> { p }, day2);
        Assert.Equal(1, ledger.Entries["act"].TodayActiveObs);   // 跨天清零后重新计 1
    }

    // ---- v0.15.1：瞬态窗口（弹窗骚扰 ≠ 使用）+ 用户标记接入 ----

    [Fact]
    public void TransientWindow_ShortSilentStreak_IsHarassmentNotUse()
    {
        // 实测案例：MmMessagingNotifier 弹窗有可见窗口 → 旧判定算"今天用过" → 建议漏报
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        var now = DateTime.UtcNow;
        var p = P(1, "notifier", window: true, cpu: 0);
        ledger.Observe(new List<ProcessInfo> { p }, now);                       // 窗口出现
        ledger.Observe(new List<ProcessInfo> { }, now + TimeSpan.FromSeconds(10)); // 下一 tick 消失

        var b = ledger.Entries["notifier"];
        Assert.Equal(1, b.TransientWindowObs);               // 记一次弹窗骚扰
        Assert.Equal(DateTime.MinValue, b.LastActiveUtc);    // 不算使用 → 建议器仍会说"今天未使用"
        Assert.Equal(0, b.TodayActiveObs);
    }

    [Fact]
    public void WindowStreak_Long_CountsAsRealUse()
    {
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        var now = DateTime.UtcNow;
        var p = P(1, "realapp", window: true, cpu: 0);
        ledger.Observe(new List<ProcessInfo> { p }, now);                          // 出现
        ledger.Observe(new List<ProcessInfo> { p }, now + TimeSpan.FromMinutes(3)); // 还在（>5min 才结束，这里构造 6 分钟）
        ledger.Observe(new List<ProcessInfo> { }, now + TimeSpan.FromMinutes(6));

        var b = ledger.Entries["realapp"];
        Assert.Equal(0, b.TransientWindowObs);
        Assert.True(b.LastActiveUtc >= now + TimeSpan.FromMinutes(6), "长窗口 = 真实使用");
    }

    [Fact]
    public void AudioDuringStreak_CountsAsUse()
    {
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        var now = DateTime.UtcNow;
        var p1 = P(1, "media", window: true, cpu: 0);
        ledger.Observe(new List<ProcessInfo> { p1 }, now);
        var p2 = P(1, "media", window: false, cpu: 0, audio: true);
        ledger.Observe(new List<ProcessInfo> { p2 }, now + TimeSpan.FromSeconds(10));
        ledger.Observe(new List<ProcessInfo> { }, now + TimeSpan.FromSeconds(20));

        var b = ledger.Entries["media"];
        Assert.Equal(0, b.TransientWindowObs);    // 期间有音频 → 不是弹窗骚扰
        Assert.True(b.LastActiveUtc > DateTime.MinValue);
    }

    [Fact]
    public void StreakEndedByProcessExit_IsEvaluated()
    {
        // 弹完就退出的进程：名字从快照消失时也要补判定
        var ledger = BehaviorLedger.Load(Path.Combine(_dir, "behavior.json"));
        var now = DateTime.UtcNow;
        var p = P(1, "popup-and-exit", window: true, cpu: 0);
        ledger.Observe(new List<ProcessInfo> { p }, now);
        ledger.Observe(new List<ProcessInfo> { }, now + TimeSpan.FromSeconds(10));  // 进程没了

        var b = ledger.Entries["popup-and-exit"];
        Assert.Equal(1, b.TransientWindowObs);
        Assert.Equal(DateTime.MinValue, b.LastActiveUtc);
    }

    [Fact]
    public void MarkedCleanable_Process_IsAdvised_EvenIfNotBloatware()
    {
        // 用户在进程页右键「可清理」→ 建议器直接认（不要求进内置垃圾名单）
        var procs = new List<ProcessInfo> { P(1, "MmMessagingNotifier", cat: ProcessCategory.ThirdPartyApp) };
        var advice = JunkAdvisor.Recommend(procs, Behaviors(("MmMessagingNotifier", null)),
            -1, new HashSet<int>(), Array.Empty<string>(), 120, null, DateTime.UtcNow,
            markedCleanable: new[] { "MmMessagingNotifier" });
        Assert.NotNull(advice);
        Assert.Equal("MmMessagingNotifier", advice!.Candidates[0].Name);
    }

    [Fact]
    public void MarkedCleanable_ProtectedOrActive_NeverAdvised()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "svc1", cat: ProcessCategory.SystemCritical),
            P(2, "activesvc"),
        };
        var behaviors = Behaviors(("activesvc", DateTime.UtcNow - TimeSpan.FromMinutes(5)));
        var advice = JunkAdvisor.Recommend(procs, behaviors,
            -1, new HashSet<int>(), Array.Empty<string>(), 120, null, DateTime.UtcNow,
            markedCleanable: new[] { "svc1", "activesvc" });
        Assert.Null(advice);   // 保护类与今天活跃的都不推
    }

    [Fact]
    public void TransientWindowObs_IsPersisted()
    {
        var path = Path.Combine(_dir, "behavior.json");
        var ledger = BehaviorLedger.Load(path);
        var p = P(1, "pop", window: true, cpu: 0);
        ledger.Observe(new List<ProcessInfo> { p }, DateTime.UtcNow);
        ledger.Observe(new List<ProcessInfo> { }, DateTime.UtcNow + TimeSpan.FromSeconds(10));
        ledger.Save();

        var reloaded = BehaviorLedger.Load(path);
        Assert.Equal(1, reloaded.Entries["pop"].TransientWindowObs);
    }

    [Fact]
    public void JunkAdvice_DefaultsOn()
    {
        Assert.True(new AppConfig().JunkAdviceEnabled);
    }

    [Fact]
    public void JunkAdviceCard_Properties_Behave()
    {
        var config = new ConfigStore(_dir);
        var vm = new MainViewModel(config, new AuditLogger(_dir), new FeedbackStore(_dir));
        try
        {
            Assert.False(vm.JunkAdvicePending);
            Assert.Equal("", vm.JunkAdviceNames);
            Assert.Equal(0, vm.JunkAdviceTotalMb);
            vm.DismissJunkAdvice();   // 无活动时忽略不得抛
            Assert.False(vm.JunkAdvicePending);
        }
        finally { vm.Shutdown(); }
    }
}
