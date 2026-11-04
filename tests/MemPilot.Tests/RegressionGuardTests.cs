using System.Text.RegularExpressions;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// v0.9.0 三轮复盘守卫：把这次修掉的 8 个真 bug 逐条锁死，防止回归。
/// 每条注释写明它对应哪次真实故障。
/// </summary>
public class RegressionGuardTests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;
    private static string Src(params string[] parts) => Path.Combine(new[] { RepoRoot(), "src" }.Concat(parts).ToArray());
    private static string Read(params string[] parts) => File.ReadAllText(Src(parts));

    [Fact]
    public void SwitchToTab_Keys_MustMatchExistingTabTags()
    {
        // 故障：v0.6.0 把路由改成 Tag 后，MakeRoomPage 仍传 "🚀"，导致热键与主页按钮 100% 崩。
        var xaml = Read("MemPilot.App", "MainWindow.xaml");
        var tags = Regex.Matches(xaml, "<TabItem Header=\"[^\"]+\" Tag=\"([^\"]+)\"")
                        .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var csFiles = Directory.GetFiles(Src("MemPilot.App"), "*.cs");
        var keys = new List<string>();
        foreach (var f in csFiles)
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "SwitchToTab\\(\"([^\"]*)\"\\)"))
                keys.Add(m.Groups[1].Value);

        Assert.NotEmpty(keys);
        foreach (var key in keys.Distinct())
            Assert.True(tags.Contains(key), $"SwitchToTab(\"{key}\") 没有对应的页签 Tag");
        Assert.DoesNotContain(keys, k => k.Any(c => c > 0x2000)); // 不允许再用 emoji 当路由键
    }

    [Fact]
    public void JunkSweeper_NeverEmitsForceKill()
    {
        // 故障：无窗口垃圾直接 L4 强杀，而 UI 的「低风险模式」写着"不会结束进程"。
        var src = Read("MemPilot.Core", "Services", "JunkSweeper.cs");
        Assert.DoesNotContain("L4ForceKill", src);
        Assert.Contains("L3GracefulClose", src);
    }

    [Fact]
    public void SingleInstance_SecondInstance_OpensExistingSignal()
    {
        // 故障：第二实例对恒为 null 的 _showSignal 调 Set()，双击 exe 毫无反应。
        var src = Read("MemPilot.App", "App.xaml.cs");
        Assert.Contains("EventWaitHandle.OpenExisting", src);
    }

    [Fact]
    public void Execute_RechecksProcessIdentity_BeforeStateChange()
    {
        // 故障：腾挪计划从生成到执行可能隔几分钟，PID 复用会杀错进程。
        var src = Read("MemPilot.Core", "Services", "CleanupEngine.cs");
        Assert.Contains("IsStillSameProcess", src);
        Assert.Contains("StartTime == p.StartTime", src);
    }

    [Fact]
    public void LeaseApi_ReservesBeforeRelief_ToAvoidOrphanedSuspends()
    {
        // 故障：并发 POST 时第二个请求的 relief 已挂起进程，但 Acquire 失败 → 无人记账、永不恢复。
        var api = Read("MemPilot.Core", "Services", "LeaseApiService.cs");
        Assert.Contains("TryReserve", api);
        Assert.Contains("CompleteReservation", api);
        var mgr = Read("MemPilot.Core", "Services", "LeaseManager.cs");
        Assert.Contains("AbortReservation", mgr);
    }

    [Fact]
    public void CleanWorkingSet_RequiresExplicitAllowList()
    {
        // 故障：全机 EmptyWorkingSet 会把游戏/系统进程一起压，且 README 承诺"前台永不处置"。
        var src = Read("MemPilot.Core", "Services", "NativeMemoryCleanerService.cs");
        Assert.Contains("SetWorkingSetAllowList", src);
        Assert.Contains("已拒绝执行", src);
    }

    [Fact]
    public void GpuMonitor_EndsAiSession_WhenSamplingDisabled()
    {
        // 故障：关掉显存采样后 AiSessionActive 永久 true → CommitReliefPolicy 永久不动作。
        var src = Read("MemPilot.Core", "Services", "GpuMonitor.cs");
        var idx = src.IndexOf("if (!_enabled())", StringComparison.Ordinal);
        Assert.True(idx > 0);
        var tail = src[idx..Math.Min(src.Length, idx + 500)];
        Assert.Contains("Session.Active", tail);
        Assert.Contains("AiSessionChanged?.Invoke", tail);
    }

    [Fact]
    public void ConfigStore_SavesSnapshot_NotLiveObject()
    {
        // 故障：Save 期间把内存里的 key 改成密文，引擎线程可能读到 "dpapi:..." 当密钥。
        var src = Read("MemPilot.Core", "Services", "ConfigStore.cs");
        Assert.Contains("_saveLock", src);
        Assert.Contains("snapshot", src);
        Assert.Contains("Dpapi.Protect(snapshot.OnlineLookupApiKey)", src);
    }

    [Fact]
    public void AutoOptimize_IsNoLongerIndependentlyScheduled()
    {
        // 决策：215 次执行里 198 次零收益，合并进 CommitRelief 一级动作。
        var src = Read("MemPilot.Core", "Services", "ResidentEngine.cs");
        Assert.DoesNotContain("AutoOptimize(mem)", src);
    }

    [Fact]
    public void CommitReliefDialog_IsNonBlocking()
    {
        // 故障：Dispatcher.Invoke（同步）+ MessageBox 会把引擎 tick 阻塞到用户点掉对话框，
        // 无人值守时整条刷新/自动清理管线永久停摆。
        var src = Read("MemPilot.App", "ViewModels", "MainViewModel.cs");
        var idx = src.IndexOf("CommitReliefL3Suggested +=", StringComparison.Ordinal);
        Assert.True(idx > 0);
        var tail = src[idx..Math.Min(src.Length, idx + 400)];
        Assert.Contains("BeginInvoke", tail);
        Assert.DoesNotContain("Dispatcher.Invoke(new Action", tail);
    }

    [Fact]
    public void AutoL3_DoesNotBlockTickThread()
    {
        // 故障：L3 内部 Thread.Sleep 轮询到超时（8s/个），在 tick 线程同步跑会让守卫失明。
        var src = Read("MemPilot.Core", "Services", "ResidentEngine.AutoActions.cs");
        var idx = src.IndexOf("AutoCloseCleanMatches", StringComparison.Ordinal);
        Assert.True(idx > 0);
        var tail = src[idx..Math.Min(src.Length, idx + 700)];
        Assert.Contains("Task.Run", tail);
    }

    [Fact]
    public void AuditLogger_BatchesWrites_InsteadOfEveryCall()
    {
        // 故障：每次动作都在锁内全量重写 2000 条，腾挪 20 项 = 20 次全量写盘。
        var src = Read("MemPilot.Core", "Services", "AuditLogger.cs");
        Assert.Contains("ScheduleFlush", src);
        Assert.Contains("public void Flush", src);
    }

    [Fact]
    public void EnumerationFailures_AreFailSafe_NotSilent()
    {
        // 故障：窗口/音频枚举失败被吞 → "无窗口"/"静音"误判 → 进程进处置候选。
        var proc = Read("MemPilot.Core", "Services", "ProcessService.cs");
        Assert.Contains("_windowEnumDegraded", proc);
        Assert.Contains("WindowEnumDegraded", proc);
        var audio = Read("MemPilot.Core", "Services", "AudioSessionService.cs");
        Assert.Contains("_degraded", audio);
    }

    [Fact]
    public void DecisionLog_CapIsRaised()
    {
        var src = Read("MemPilot.Core", "Services", "DecisionLog.cs");
        Assert.Contains("MaxEntries = 5000", src);
    }
}
