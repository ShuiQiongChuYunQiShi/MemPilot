using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>Safety invariants of the unused detector: nothing gets flagged without
/// minimum age, an observation streak, and clean category/eligibility.</summary>
public class UnusedDetectorTests
{
    private static AppConfig Config() => new()
    {
        UnusedCpuThresholdPercent = 2,
        UnusedIdleMinutes = 0,
        MinProcessAgeSeconds = 120,
        RequiredIdleObservations = 3,
        RefreshIntervalSeconds = 10,
        Whitelist = new List<string>(),
        Blacklist = new List<string>()
    };

    // Note: StartTime must be pinned per scenario — the state key is (pid, startTime),
    // so a fresh "now - 2h" on every call would defeat the streak on purpose.
    private static readonly DateTime OldStart = DateTime.Now.AddHours(-2);

    private static ProcessInfo MakeProc(int pid, string name, ProcessCategory category,
        bool hasWindow = false, DateTime? start = null, int children = 0)
        => new()
        {
            Pid = pid,
            Name = name,
            ExecutablePath = $@"D:\apps\{name}.exe",
            StartTime = start ?? OldStart,
            Category = category,
            CategoryReason = "test",
            HasVisibleWindow = hasWindow,
            ChildPids = Enumerable.Range(1, children).Select(i => pid + i).ToList(),
            Memory = new MemoryMetrics
            {
                TotalCpuTime = TimeSpan.Zero,
                IoReadBytes = 0,
                IoWriteBytes = 0,
                PrivateWorkingSetBytes = 256L * 1024 * 1024,
                PrivateBytes = 512L * 1024 * 1024,
                WorkingSetBytes = 300L * 1024 * 1024
            }
        };

    private static readonly SystemMemoryInfo Mem = new()
    {
        TotalPhysicalBytes = 32L * 1024 * 1024 * 1024,
        AvailablePhysicalBytes = 4L * 1024 * 1024 * 1024,
        TotalCommitLimitBytes = 48L * 1024 * 1024 * 1024,
        TotalCommittedBytes = 20L * 1024 * 1024 * 1024
    };

    private static List<UnusedCandidate> Detect(UnusedDetector d, List<ProcessInfo> procs,
        int fg = 0, HashSet<int>? audio = null, HashSet<int>? suspended = null)
        => d.Detect(procs, Mem, fg, audio ?? new HashSet<int>(), suspended ?? new HashSet<int>());

    [Fact]
    public void IdleProcess_BecomesCandidate_OnlyAfterObservationStreak()
    {
        var detector = new UnusedDetector { Config = Config() };
        for (int round = 1; round <= 2; round++)
        {
            var result = Detect(detector, new List<ProcessInfo> { MakeProc(10, "bgsvc", ProcessCategory.BackgroundService) });
            Assert.Empty(result); // streak 1..2 < RequiredIdleObservations(3)
        }
        var third = Detect(detector, new List<ProcessInfo> { MakeProc(10, "bgsvc", ProcessCategory.BackgroundService) });
        Assert.Single(third);
        Assert.Equal(CleanupActionLevel.L1Trim, third[0].SuggestedLevel);
    }

    [Fact]
    public void WindowedProcess_NeverBecomesCandidate()
    {
        var detector = new UnusedDetector { Config = Config() };
        for (int i = 0; i < 6; i++)
        {
            var result = Detect(detector, new List<ProcessInfo> { MakeProc(11, "editor", ProcessCategory.ThirdPartyApp, hasWindow: true) });
            Assert.Empty(result);
        }
    }

    [Fact]
    public void YoungProcess_NeverBecomesCandidate()
    {
        var detector = new UnusedDetector { Config = Config() };
        var young = DateTime.Now.AddSeconds(-5);
        for (int i = 0; i < 6; i++)
        {
            var result = Detect(detector, new List<ProcessInfo> { MakeProc(12, "newapp", ProcessCategory.ThirdPartyApp, start: young) });
            Assert.Empty(result);
        }
    }

    [Fact]
    public void UnknownCategory_NeverBecomesCandidate()
    {
        var detector = new UnusedDetector { Config = Config() };
        for (int i = 0; i < 6; i++)
        {
            var result = Detect(detector, new List<ProcessInfo> { MakeProc(13, "mystery", ProcessCategory.Unknown) });
            Assert.Empty(result);
        }
    }

    [Fact]
    public void AudioActiveProcess_NeverBecomesCandidate()
    {
        var detector = new UnusedDetector { Config = Config() };
        var audio = new HashSet<int> { 14 };
        for (int i = 0; i < 6; i++)
        {
            var result = Detect(detector, new List<ProcessInfo> { MakeProc(14, "musicplayer", ProcessCategory.ThirdPartyApp) }, audio: audio);
            Assert.Empty(result);
        }
    }

    [Fact]
    public void ForegroundProcess_NeverBecomesCandidate()
    {
        var detector = new UnusedDetector { Config = Config() };
        for (int i = 0; i < 6; i++)
        {
            var result = Detect(detector, new List<ProcessInfo> { MakeProc(15, "focus", ProcessCategory.ThirdPartyApp) }, fg: 15);
            Assert.Empty(result);
        }
    }

    [Fact]
    public void SystemCritical_NeverBecomesCandidate()
    {
        var detector = new UnusedDetector { Config = Config() };
        for (int i = 0; i < 6; i++)
        {
            var csrss = MakeProc(16, "csrss", ProcessCategory.SystemCritical);
            var result = Detect(detector, new List<ProcessInfo> { csrss });
            Assert.Empty(result);
        }
    }

    [Fact]
    public void PidReuse_ResetsObservationStreak()
    {
        var detector = new UnusedDetector { Config = Config() };
        for (int i = 0; i < 5; i++)
            Detect(detector, new List<ProcessInfo> { MakeProc(17, "victim", ProcessCategory.ThirdPartyApp) });

        // same pid, different start time = a different process; streak must restart
        var rebornStart = DateTime.Now.AddHours(-1); // old enough to pass the min-age gate
        var reborn = new List<ProcessInfo> { MakeProc(17, "victim", ProcessCategory.ThirdPartyApp, start: rebornStart) };
        Assert.Empty(Detect(detector, reborn));
    }

    [Fact]
    public void BlacklistedProcess_NeedsOnlyTwoObservations_AndGetsAggressiveLevel()
    {
        var cfg = Config();
        cfg.Blacklist = new List<string> { "mybgsvc" };
        var detector = new UnusedDetector { Config = cfg };
        Func<ProcessInfo> proc = () => MakeProc(18, "MyBgSvc", ProcessCategory.ThirdPartyApp);

        Assert.Empty(Detect(detector, new List<ProcessInfo> { proc() })); // 1st observation: grace

        var second = Detect(detector, new List<ProcessInfo> { proc() });
        Assert.Single(second);
        Assert.Equal(CleanupActionLevel.L4ForceKill, second[0].SuggestedLevel); // headless -> kill suggested
        Assert.Equal(ProcessRiskLevel.Medium, second[0].Risk);
    }

    [Fact]
    public void WhitelistedProcess_NeverBecomesCandidate()
    {
        var cfg = Config();
        cfg.Whitelist = new List<string> { "keeper" };
        var detector = new UnusedDetector { Config = cfg };
        for (int i = 0; i < 6; i++)
        {
            var result = Detect(detector, new List<ProcessInfo> { MakeProc(19, "keeper", ProcessCategory.ThirdPartyApp) });
            Assert.Empty(result);
        }
    }

    [Fact]
    public void BusyHeadlessProcess_NeverBecomesCandidate()
    {
        // regression: LastSeenUtc was updated before the rate computation, making dt always 0
        // and every busy headless process (compile servers etc.) look idle.
        var detector = new UnusedDetector { Config = Config() };
        for (int round = 0; round < 10; round++)
        {
            var p = MakeProc(21, "langserver", ProcessCategory.ThirdPartyApp);
            p.Memory.TotalCpuTime = TimeSpan.FromSeconds(10 * (round + 1)); // busy every round
            p.Memory.IoWriteBytes = 1024L * 1024 * 1024 * (round + 1);
            var result = Detect(detector, new List<ProcessInfo> { p });
            if (round > 0) // first round has no baseline to diff against
                Assert.True(p.Memory.CpuPercent > 50, $"CPU% must reflect real usage in round {round}");
            Assert.Empty(result);
        }
    }

    [Fact]
    public void MemoryGrowth_IsTrackedAcrossObservations()
    {
        var detector = new UnusedDetector { Config = Config() };
        var start = 100L * 1024 * 1024;
        for (int i = 0; i < 5; i++)
        {
            var p = MakeProc(20, "growing", ProcessCategory.ThirdPartyApp);
            p.Memory.WorkingSetBytes = start + i * 200L * 1024 * 1024; // +200MB per round
            p.Memory.PrivateWorkingSetBytes = p.Memory.WorkingSetBytes;
            var result = Detect(detector, new List<ProcessInfo> { p });
            // growth is display-only metadata; candidacy is not the point here
            Assert.True(p.MemoryGrowthBytes >= 0);
        }
        var last = MakeProc(20, "growing", ProcessCategory.ThirdPartyApp);
        last.Memory.WorkingSetBytes = start + 4 * 200L * 1024 * 1024;
        Detect(detector, new List<ProcessInfo> { last });
        Assert.Equal(4 * 200L * 1024 * 1024, last.MemoryGrowthBytes); // 100MB -> 900MB over window
    }
}
