using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>垃圾驻守清扫器的安全边界：能被清的必须清，不该碰的绝不进计划。</summary>
public class JunkSweeperTests
{
    private static ProcessInfo P(int pid, string name, ProcessCategory cat, bool window = false,
        bool svc = false, bool prot = false, bool sys = false, bool audio = false, int ageMin = 10)
        => new()
        {
            Pid = pid,
            Name = name,
            Category = cat,
            HasVisibleWindow = window,
            IsService = svc,
            IsProtected = prot,
            IsSystemProcess = sys,
            AudioActive = audio,
            StartTime = DateTime.Now.AddMinutes(-ageMin),
            ExecutablePath = $@"C:\apps\{name}.exe"
        };

    private static List<JunkSweepItem> Plan(List<ProcessInfo> procs, int fg = 0,
        HashSet<int>? audio = null, string[]? whitelist = null)
        => JunkSweeper.BuildPlan(procs, fg, audio ?? new HashSet<int>(),
            whitelist ?? Array.Empty<string>(), minAgeSeconds: 120);

    [Fact]
    public void BloatwareHeadless_IsKilled()
    {
        var plan = Plan(new() { P(1, "wetype_update", ProcessCategory.Bloatware) });
        var item = Assert.Single(plan);
        Assert.Equal(CleanupActionLevel.L4ForceKill, item.Action);
        Assert.Contains("垃圾常驻", item.Reason);
    }

    [Fact]
    public void BloatwareWithWindow_IsOnlyTrimmed()
    {
        var plan = Plan(new() { P(1, "wps", ProcessCategory.Bloatware, window: true) });
        var item = Assert.Single(plan);
        Assert.Equal(CleanupActionLevel.L1Trim, item.Action);
        Assert.Contains("仅回收工作集", item.Reason);
    }

    [Fact]
    public void OrphanResidual_WithoutHost_IsKilled()
    {
        var plan = Plan(new()
        {
            P(1, "QQLiveService", ProcessCategory.ThirdPartyApp),
            P(2, "explorer", ProcessCategory.SystemService, sys: true),
        });
        var item = Assert.Single(plan);
        Assert.Equal("QQLiveService", item.Process.Name);
        Assert.Equal(CleanupActionLevel.L4ForceKill, item.Action);
        Assert.Contains("宿主 qqlive 已退出", item.Reason);
    }

    [Fact]
    public void Residual_WithHostAlive_IsNotInPlan()
    {
        var plan = Plan(new()
        {
            P(1, "QQLiveService", ProcessCategory.ThirdPartyApp),
            P(2, "QQLive", ProcessCategory.ThirdPartyApp),
        });
        Assert.DoesNotContain(plan, i => i.Process.Name == "QQLiveService");
    }

    [Fact]
    public void HardExclusions_ServicesProtectedSecurityAnticheatDriverUnknown()
    {
        var plan = Plan(new()
        {
            P(1, "alibabaprotect", ProcessCategory.Bloatware, svc: true),      // Windows 服务不杀
            P(2, "wpscenter", ProcessCategory.Bloatware, prot: true),         // 受保护
            P(3, "eviljunk", ProcessCategory.SecuritySoftware),               // 安全软件
            P(4, "ace-helper", ProcessCategory.AntiCheat),                    // 反作弊
            P(5, "somedriver", ProcessCategory.Driver),                       // 驱动
            P(6, "mystery", ProcessCategory.Unknown),                         // 未识别
        });
        Assert.Empty(plan);
    }

    // 无界面 ≠ 垃圾：AI 工具 / 开发工具 / 容器 VM 即使常驻无窗口也永不清扫
    [Fact]
    public void HeadlessWorkloads_AreNeverSwept()
    {
        var plan = Plan(new()
        {
            P(1, "llama-server", ProcessCategory.AiImageVideo),
            P(2, "mysqld", ProcessCategory.DevTool),
            P(3, "opencode-cli", ProcessCategory.DevTool),
            P(4, "docker", ProcessCategory.ContainerVm),
            P(5, "node", ProcessCategory.DevTool),
        });
        Assert.Empty(plan);
    }

    // 微信输入法：引擎三件套是 ThirdPartyApp 永不清扫，只有更新器 wetype_update 进计划
    [Fact]
    public void WeTypeImeEngine_IsNeverSwept_OnlyUpdaterIs()
    {
        var plan = Plan(new()
        {
            P(1, "wetype_server", ProcessCategory.ThirdPartyApp),
            P(2, "wetype_renderer", ProcessCategory.ThirdPartyApp),
            P(3, "wetype_service", ProcessCategory.ThirdPartyApp),
            P(4, "wetype_update", ProcessCategory.Bloatware),
        });
        var item = Assert.Single(plan);
        Assert.Equal("wetype_update", item.Process.Name);
    }

    [Fact]
    public void OrphansOnly_LowRiskMode_SkipsBloatware_KeepsOrphans()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "wetype_update", ProcessCategory.Bloatware),          // 常驻垃圾：低风险模式跳过
            P(2, "QQLiveService", ProcessCategory.ThirdPartyApp),      // 孤儿残留：保留
        };
        var plan = JunkSweeper.BuildPlan(procs, 0, new HashSet<int>(), Array.Empty<string>(),
            minAgeSeconds: 120, orphansOnly: true);
        var item = Assert.Single(plan);
        Assert.Equal("QQLiveService", item.Process.Name);
    }

    [Fact]
    public void ForegroundAudioYoungWhitelisted_AreExcluded()
    {
        var procs = new List<ProcessInfo>
        {
            P(1, "wpscenter", ProcessCategory.Bloatware),                  // foreground
            P(2, "wpscloudsvr", ProcessCategory.Bloatware, audio: true),   // 有声
            P(3, "lghub_updater", ProcessCategory.Bloatware, ageMin: 0),   // 太年轻
            P(4, "wpsnotify", ProcessCategory.Bloatware),                  // 白名单
        };
        var plan = Plan(procs, fg: 1, whitelist: new[] { "wpsnotify" });
        Assert.Empty(plan);
    }
}
