using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.10.0 开源前正确性守卫。分三类，每类对应一个"日志/界面对用户说谎"的真实缺陷：
///
/// 1. **权限诚实性**——非提权时清备用列表必然失败，不能写"执行"；
/// 2. **反馈不能穿透安全不变式**——把 Windows 服务标记为"可清理"不能让 IsService 失效；
/// 3. **默认值保守**——陌生人装上就该什么都不做。
///
/// 全部是静态/纯函数断言，不需要真实提权或真实服务表。
/// </summary>
public class OpenSourceReadinessTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;
    private static string Src(params string[] parts)
        => Path.Combine(new[] { RepoRoot(), "src" }.Concat(parts).ToArray());
    private static string Read(params string[] parts) => File.ReadAllText(Src(parts));

    // ---------------- 1. 权限诚实性 ----------------

    [Fact]
    public void CommitRelief_ChecksAdmin_BeforeClaimingExecution()
    {
        // 故障：ResidentEngine.Pressure.cs 调 CleanAreas 后丢弃返回值，Decision 仍写"执行"。
        // 标准用户下 NtSetSystemInformation 返回 STATUS_PRIVILEGE_NOT_HELD，什么都没做。
        var src = Read("MemPilot.Core", "Services", "ResidentEngine.Pressure.cs");

        Assert.Contains("IsAdmin()", src);
        Assert.Contains("decision = \"跳过\"", src);

        // CleanAreas 的返回值必须被读
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"var\s+purge\s*=\s*_memCleaner\.CleanAreas"), src);
        Assert.Contains("purge.TryGetValue", src);
    }

    [Fact]
    public void CommitRelief_NeverWritesExecutionUnconditionally()
    {
        // 曾经的写法：Decision = "执行" 直接写死，与是否真执行无关。
        // 现在必须走 decision 变量（由权限与返回值决定）。
        var src = Read("MemPilot.Core", "Services", "ResidentEngine.Pressure.cs");
        var idx = src.IndexOf("Kind = \"CommitRelief\"", StringComparison.Ordinal);
        Assert.True(idx > 0);
        var tail = src[idx..Math.Min(src.Length, idx + 400)];
        Assert.Contains("Decision = decision", tail);
        Assert.DoesNotContain("Decision = \"执行\"", tail);
    }

    [Fact]
    public void LeaseRelief_AlsoChecksAdmin()
    {
        // 同一条规则在租约路径上也要守：租约响应会告诉调用方"已清备用列表"，
        // 非提权时那同样是假话。
        // v0.14：ExecuteLeaseRelief 拆分到 MainViewModel.Lease.cs
        var src = Read("MemPilot.App", "ViewModels", "MainViewModel.Lease.cs");
        int idx = src.IndexOf("ExecuteLeaseRelief", StringComparison.Ordinal);
        Assert.True(idx > 0);
        var tail = src[idx..Math.Min(src.Length, idx + 1400)];
        Assert.Contains("IsAdmin()", tail);
        Assert.Contains("未提权，跳过备用列表清理", tail);
    }

    // ---------------- 2. 反馈不能穿透安全不变式 ----------------

    [Fact]
    public void Classification_HardSafetyRunsBeforeFeedback()
    {
        // 故障：反馈命中就 early return，跳过服务/驱动检测 → IsService=false
        // → UnusedDetector / JunkSweeper 的 IsService 排除失效。
        var src = Read("MemPilot.Core", "Services", "ClassificationEngine.cs");

        int hard = src.IndexOf("硬安全判定", StringComparison.Ordinal);
        int feedback = src.IndexOf("var fb = _feedback.Find", StringComparison.Ordinal);
        Assert.True(hard > 0, "找不到硬安全判定段");
        Assert.True(feedback > 0, "找不到反馈查询");
        Assert.True(hard < feedback,
            "硬安全判定（服务/驱动/反作弊/系统关键/安全软件）必须排在用户反馈之前");
    }

    [Fact]
    public void Classification_FeedbackCannotChangeProtectedCategory()
    {
        var src = Read("MemPilot.Core", "Services", "ClassificationEngine.cs");
        // 硬安全分支里不得出现"用反馈覆盖 Category"的写法
        var hard = src.IndexOf("硬安全判定", StringComparison.Ordinal);
        var fb = src.IndexOf("var fb = _feedback.Find", StringComparison.Ordinal);
        var block = src[hard..fb];
        Assert.DoesNotContain("p.Category = fb.ReportedCategory", block);
        Assert.Contains("ApplyFeedbackLabels", block);
    }

    [Fact]
    public void MarkingServiceCleanable_StillIsServiceAndUntouchable()
    {
        // 行为断言（不只是源码断言）：把一个"服务"标记为可清理，
        // 分类结果仍必须是服务且受保护。
        using var tmp = new TempDir();
        var feedback = new FeedbackStore(tmp.Path);
        feedback.Add(new FeedbackEntry
        {
            ProcessName = "svchost",
            ExecutablePath = @"C:\Windows\System32\svchost.exe",
            MarkCleanable = true,
            Status = "applied"
        });

        var engine = new ClassificationEngine(feedback, null);
        var p = new ProcessInfo
        {
            Pid = 4242,
            Name = "svchost",
            ExecutablePath = @"C:\Windows\System32\svchost.exe"
        };
        var services = new Dictionary<int, List<ServiceInfo>>
        {
            [4242] = new List<ServiceInfo> { new() { Name = "RpcSs", IsDriver = false } }
        };

        engine.Classify(p, services, new HashSet<string>());

        Assert.True(p.IsService, "被标记可清理的服务仍然是服务");
        Assert.True(p.IsBlacklisted, "用户的标记要能显示出来（供 UI 说明）");
    }

    // ---------------- 3. 默认值保守 ----------------

    [Fact]
    public void Defaults_AreObserveOnly_NobodyGetsSurprised()
    {
        // 陌生人装上、不看设置、直接用 —— 必须什么都不做。
        var cfg = new AppConfig();

        Assert.False(cfg.LeaseApiEnabled, "租约 API 默认不能监听端口");
        Assert.False(cfg.AutoCommitReliefEnabled, "提交兜底默认不能自动压缩别人的工作集");
        Assert.False(cfg.AutoCleanupEnabled, "自动清理默认关");
        Assert.False(cfg.AutoJunkSweepEnabled, "垃圾清扫默认关");
        Assert.False(cfg.AutoCloseCleanMatches, "自动优雅关闭默认关");
        Assert.False(cfg.CommitReliefL3Enabled, "自动关程序默认关");
        Assert.False(cfg.AutoModeSwitch, "自动切模式默认关");
        Assert.False(cfg.StartWithWindows, "默认不写开机启动");
    }

    [Fact]
    public void DeadAutoOptimize_IsGoneFromEngine()
    {
        // 故障：方法从未被 AfterRefresh 调用，设置页两个复选框点了没反应。
        var engine = Read("MemPilot.Core", "Services", "ResidentEngine.AutoActions.cs");
        Assert.DoesNotContain("private void AutoOptimize(", engine);

        var xaml = Read("MemPilot.App", "MainWindow.xaml");
        Assert.DoesNotContain("SetAutoOptimize\"", xaml);
        Assert.DoesNotContain("SetAutoOptimizeInterval", xaml);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(TestTmp.Dir, "osr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
