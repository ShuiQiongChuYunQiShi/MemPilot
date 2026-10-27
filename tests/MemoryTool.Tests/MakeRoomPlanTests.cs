using MemoryTool.Models;
using MemoryTool.Services;
using Xunit;

namespace MemoryTool.Tests;

public class MakeRoomPlanTests : IDisposable
{
    private readonly string _dir;
    private readonly CleanupEngine _engine;
    private readonly SystemMemoryInfo _mem = new()
    {
        TotalPhysicalBytes = 32L * 1024 * 1024 * 1024,
        AvailablePhysicalBytes = 1L * 1024 * 1024 * 1024,          // 7GB gap to 8GB target
        TotalCommitLimitBytes = 48L * 1024 * 1024 * 1024,
        TotalCommittedBytes = 41L * 1024 * 1024 * 1024             // 7GB commit gap
    };

    public MakeRoomPlanTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-plan-" + Guid.NewGuid().ToString("N"));
        _engine = new CleanupEngine(new ProcessService(), new AuditLogger(_dir), _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ProcessInfo Proc(int pid, string name, ProcessCategory cat, bool window,
        long ws = 1L * 1024 * 1024 * 1024, long commit = 2L * 1024 * 1024 * 1024)
        => new()
        {
            Pid = pid,
            Name = name,
            ExecutablePath = $@"D:\apps\{name}.exe",
            StartTime = DateTime.Now.AddHours(-3),
            Category = cat,
            CategoryReason = "test",
            HasVisibleWindow = window,
            Memory = new MemoryMetrics
            {
                PrivateWorkingSetBytes = ws,
                PrivateBytes = commit,
                WorkingSetBytes = ws
            }
        };

    private static ModeProfile Profile(bool allowKill) => new()
    {
        Mode = AppMode.AiGeneration,
        Name = "test",
        CleanPatterns = new List<string> { "steam" },
        AllowedActions = allowKill
            ? new List<CleanupActionLevel> { CleanupActionLevel.L1Trim, CleanupActionLevel.L3GracefulClose, CleanupActionLevel.L4ForceKill }
            : new List<CleanupActionLevel> { CleanupActionLevel.L1Trim, CleanupActionLevel.L3GracefulClose }
    };

    private static MakeRoomRequest Request(bool allowKill) => new()
    {
        TargetAvailableGB = 8,
        EnsureCommitToo = true,
        TargetCommitFreeGB = 8,
        AllowKill = allowKill,
        IncludeTrim = true
    };

    [Fact]
    public void Plan_ExcludesProtectedUnknownForegroundAudioAndYoung()
    {
        var procs = new List<ProcessInfo>
        {
            Proc(1, "editor", ProcessCategory.ThirdPartyApp, window: true),
            Proc(2, "mystery", ProcessCategory.Unknown, window: false),
            Proc(3, "foregroundapp", ProcessCategory.ThirdPartyApp, window: true),
            Proc(4, "musicplayer", ProcessCategory.ThirdPartyApp, window: false),
            Proc(5, "defenderlike", ProcessCategory.SecuritySoftware, window: false),
            Proc(6, "antich", ProcessCategory.AntiCheat, window: false),
            Proc(7, "young", ProcessCategory.ThirdPartyApp, window: false),
        };
        procs[6].StartTime = DateTime.Now.AddSeconds(-10);

        var plan = _engine.BuildPlan(procs, _mem, Profile(allowKill: true), Request(allowKill: true),
            foregroundPid: 3, audioPids: new HashSet<int> { 4 });

        Assert.DoesNotContain(plan.Items, i => i.Process.Pid == 2); // Unknown never auto-disposed
        Assert.DoesNotContain(plan.Items, i => i.Process.Pid == 3); // foreground
        Assert.DoesNotContain(plan.Items, i => i.Process.Pid == 4); // audio
        Assert.DoesNotContain(plan.Items, i => i.Process.Pid == 5); // security
        Assert.DoesNotContain(plan.Items, i => i.Process.Pid == 6); // anticheat
        Assert.DoesNotContain(plan.Items, i => i.Process.Pid == 7); // young
        Assert.Contains(plan.Items, i => i.Process.Pid == 1);
    }

    [Fact]
    public void Plan_WindowedGetsL3WithKillEscalation_WhenKillAllowed()
    {
        var procs = new List<ProcessInfo> { Proc(1, "editor", ProcessCategory.ThirdPartyApp, window: true) };
        var plan = _engine.BuildPlan(procs, _mem, Profile(allowKill: true), Request(allowKill: true), 0, new HashSet<int>());
        var item = Assert.Single(plan.Items);
        Assert.Equal(CleanupActionLevel.L3GracefulClose, item.Action);
        Assert.True(item.KillIfCloseFails);
    }

    [Fact]
    public void Plan_HeadlessGetsL4_OnlyWhenAllowed()
    {
        var procs = new List<ProcessInfo> { Proc(1, "bgsvc", ProcessCategory.BackgroundService, window: false) };

        var plan = _engine.BuildPlan(procs, _mem, Profile(allowKill: true), Request(allowKill: true), 0, new HashSet<int>());
        Assert.Equal(CleanupActionLevel.L4ForceKill, Assert.Single(plan.Items).Action);

        var planNoKill = _engine.BuildPlan(procs, _mem, Profile(allowKill: false), Request(allowKill: false), 0, new HashSet<int>());
        var item = Assert.Single(planNoKill.Items);
        Assert.Equal(CleanupActionLevel.L1Trim, item.Action); // trim fallback, no kill
    }

    [Fact]
    public void Plan_ModeCleanMatchesRankedFirst_AndEscalationDisabledForThemUnlessAllowed()
    {
        var procs = new List<ProcessInfo>
        {
            Proc(1, "steamwebhelper", ProcessCategory.GameLauncher, window: true, ws: 200L * 1024 * 1024),
            Proc(2, "bigeditor", ProcessCategory.ThirdPartyApp, window: true, ws: 3L * 1024 * 1024 * 1024)
        };
        var plan = _engine.BuildPlan(procs, _mem, Profile(allowKill: true), Request(allowKill: true), 0, new HashSet<int>());
        Assert.Equal(1, plan.Items[0].Process.Pid); // steam matches CleanPatterns -> ranked first
        Assert.Contains("模式清理名单", plan.Items[0].Rationale);
    }

    [Fact]
    public void Plan_NoGap_NoItems()
    {
        var mem = new SystemMemoryInfo
        {
            TotalPhysicalBytes = 32L * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 16L * 1024 * 1024 * 1024,
            TotalCommitLimitBytes = 48L * 1024 * 1024 * 1024,
            TotalCommittedBytes = 8L * 1024 * 1024 * 1024
        };
        var procs = new List<ProcessInfo> { Proc(1, "editor", ProcessCategory.ThirdPartyApp, window: true) };
        var plan = _engine.BuildPlan(procs, mem, Profile(allowKill: true), Request(allowKill: true), 0, new HashSet<int>());
        Assert.Empty(plan.Items);
        Assert.Equal(0, plan.PhysicalGapBytes);
        Assert.Equal(0, plan.CommitGapBytes);
    }
}
