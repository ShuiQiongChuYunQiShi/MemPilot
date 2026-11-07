using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 第四轮复盘守卫（v0.13.0）。每条对应一个从 11 天运行数据里挖出的真实根因：
/// 1. JunkSweeper 门控反转（有窗口→L1、无窗口→L3，实测 4 次优雅关闭全部 8s 超时）
/// 2. 失败动作无条件吃冷却（一次 0xC0000061 占住救援梯，TrimSafe 再没跟上）
/// 3. L3 对无窗口进程是空操作（GracefulClose 烧满超时）
/// 4. AI 会话被重启劈成两段（11 个"开始"无"结束"全是重启 artifact）
/// 5. 低可用兜底触发器（可用<3GB / 提交余量<10% → 直接回收，抓住被错过的危机时刻）
/// 6. FeedbackStore 并发（UI 写与引擎后台读竞态）
/// 7. 暗色主题换色表全覆盖（v0.12 只换了 17/38 个 token）
/// </summary>
public class Retrospective4Tests
{
    private static string RepoRoot() => Directory.GetParent(TestTmp.Dir)!.FullName;
    private static string Src(params string[] parts)
        => Path.Combine(new[] { RepoRoot(), "src" }.Concat(parts).ToArray());
    private static string Read(params string[] parts) => File.ReadAllText(Src(parts));

    // ---- 1. JunkSweeper 门控方向 ----

    [Fact]
    public void JunkSweeper_WindowedGetsGracefulClose_WindowlessNeverDoes()
    {
        var cfg = new AppConfig();
        var windowed = new ProcessInfo { Pid = 1, Name = "somesvc", HasVisibleWindow = true, Category = ProcessCategory.Bloatware };
        var windowless = new ProcessInfo { Pid = 2, Name = "somesvc", HasVisibleWindow = false, Category = ProcessCategory.Bloatware };

        var plan = JunkSweeper.BuildPlan(
            new List<ProcessInfo> { windowed, windowless }, foregroundPid: -1, audioPids: new HashSet<int>(),
            whitelist: new HashSet<string>(), minAgeSeconds: 0, orphansOnly: false);

        var w = plan.FirstOrDefault(i => i.Process.Pid == 1);
        var n = plan.FirstOrDefault(i => i.Process.Pid == 2);
        Assert.NotNull(w);
        Assert.NotNull(n);
        Assert.Equal(CleanupActionLevel.L3GracefulClose, w!.Action);   // 有窗口才能响应 WM_CLOSE
        Assert.NotEqual(CleanupActionLevel.L3GracefulClose, n!.Action); // 无窗口 → L3 是空操作
    }

    // ---- 2. 失败不吃冷却 ----

    [Fact]
    public void CommitRelief_FailureDoesNotConsumeCooldown()
    {
        // 证据：11-04 15:53 起 4 次 PurgeStandby 失败期间，TrimSafe 一次都没跟上——
        // _lastReliefUtc 无条件赋值让失败动作占住 2 分钟升级窗口。
        var src = Read("MemPilot.Core", "Services", "ResidentEngine.Pressure.cs");
        Assert.Contains("已立即升级安全子集压缩", src);   // 失败 → 本 tick 立即 TrimSafe
        int idx = src.IndexOf("if (reliefDelivered)", StringComparison.Ordinal);
        Assert.True(idx > 0, "冷却赋值必须收进 if (reliefDelivered) 分支");
        // 冷却赋值必须出现在该分支内部（其后 200 字符内），而不是方法尾部无条件执行
        var after = src[idx..Math.Min(src.Length, idx + 200)];
        Assert.Contains("_lastReliefUtc = DateTime.UtcNow;", after);
    }

    // ---- 3. L3 空句柄快速失败 ----

    [Fact]
    public void GracefulClose_FailsFast_WhenNoVisibleWindow()
    {
        var src = Read("MemPilot.Core", "Services", "CleanupEngine.cs");
        var idx = src.IndexOf("private (bool exited, string message) GracefulClose", StringComparison.Ordinal);
        Assert.True(idx > 0);
        var body = src[idx..Math.Min(src.Length, idx + 700)];
        Assert.Contains("HasVisibleWindows", body);
        Assert.Contains("无可见窗口", body);
    }

    [Fact]
    public void L3Candidates_RequireVisibleWindow()
    {
        var src = Read("MemPilot.Core", "Services", "ResidentEngine.Pressure.cs");
        Assert.Contains("safeSet.Where(p => p.HasVisibleWindow)", src);
    }

    // ---- 4. AI 会话持久化 ----

    [Fact]
    public void AiSession_RoundTripsAcrossRestore()
    {
        var d = new AiSessionDetector();
        // 攒够开始判定（3/5 ≥8GB）
        long g = 1024L * 1024 * 1024;
        var t0 = DateTime.UtcNow;
        d.Push(9 * g, 16 * g, t0);
        d.Push(10 * g, 16 * g, t0.AddSeconds(30));
        d.Push(9 * g, 16 * g, t0.AddSeconds(60));
        Assert.True(d.Active);
        d.Push(10 * g, 16 * g, t0.AddSeconds(90));   // 开始后峰值仍在涨（peak=10GB）
        var exported = d.ExportState();
        Assert.NotNull(exported);

        // 模拟重启：新实例恢复
        var d2 = new AiSessionDetector();
        Assert.True(d2.TryRestore(exported!));
        Assert.True(d2.Active);
        Assert.Equal(exported.StartUtc, d2.Current!.StartUtc);

        // 恢复后低显存 quorum 能闭合**原始**会话
        var t1 = t0.AddMinutes(10);
        AiSessionUpdate update = new(AiSessionEvent.None, null);
        update = d2.Push(1 * g, 16 * g, t1);
        update = d2.Push(1 * g, 16 * g, t1.AddSeconds(30));
        update = d2.Push(1 * g, 16 * g, t1.AddSeconds(60));
        Assert.Equal(AiSessionEvent.Ended, update.Event);
        Assert.Equal(exported.StartUtc, update.Summary!.StartUtc);   // 闭合的是原会话
        Assert.Equal(10 * g, update.Summary!.PeakUsedBytes);         // 峰值不丢
    }

    [Fact]
    public void AiSession_TryRestore_IsIdempotentAndRejectsClosed()
    {
        var d = new AiSessionDetector();
        var closed = new AiSessionSummary { StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
        Assert.False(d.TryRestore(closed), "已闭合会话不恢复");
        d.TryRestore(new AiSessionSummary { StartUtc = DateTime.UtcNow });
        Assert.False(d.TryRestore(new AiSessionSummary { StartUtc = DateTime.UtcNow }), "已活跃时幂等拒绝");
    }

    [Fact]
    public void GpuMonitor_WiresSessionPersistence()
    {
        var src = Read("MemPilot.Core", "Services", "GpuMonitor.cs");
        Assert.Contains("TryRestoreSession", src);
        Assert.Contains("SaveSessionState(update.Summary)", src);
        Assert.Contains("SaveSessionState(null)", src);
        Assert.Contains("ai-session.json", src);
    }

    // ---- 5. 低可用兜底 ----

    [Fact]
    public void AvailableFloor_TriggerExists_WithDedupAndFullscreenGate()
    {
        var src = Read("MemPilot.Core", "Services", "ResidentEngine.Pressure.cs");
        Assert.Contains("WatchAvailableFloor", src);
        Assert.Contains("availGb < 3.0", src);
        Assert.Contains("TotalMinutes < 10", src);       // 10 分钟去重
        Assert.Contains("if (ForegroundFullscreen) return;", src);
        Assert.Contains("FloorTrim", src);
    }

    // ---- 6. FeedbackStore 线程安全 ----

    [Fact]
    public async Task FeedbackStore_ConcurrentReadWhileWriting_DoesNotThrow()
    {
        using var tmp = new TempDir();
        var store = new FeedbackStore(tmp.Path);
        var cts = new CancellationTokenSource(1500);
        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                store.Add(new FeedbackEntry { ProcessName = $"p{i % 7}", MarkCleanable = true });
                if (i % 3 == 0) store.SetWorkload($"p{i % 7}", "", WorkloadKind.Work);
                if (i % 5 == 0 && store.All().Count > 4)
                    store.Remove(store.All()[0].Id);
                i++;
            }
        });
        // 引擎线程的读热路径：Find/FindWorkload 在写线程狂奔时不得抛枚举异常
        var readErr = await Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    store.Find("p1", "", "");
                    store.FindWorkload("p2", "");
                    store.All();
                }
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });
        await writer;
        Assert.Null(readErr);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(TestTmp.Dir, "r4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    // ---- 7. 暗色主题 token 全覆盖 ----

    [Fact]
    public void DarkTheme_SwapTableCoversAllReferencedTokens()
    {
        // v0.12.0 只换了 17 个 token，v0.12 新增的 21 个不在表里——切暗色会花。
        // 守卫：ToggleTheme 里的 Set/SetA 键集合 ⊇ App.xaml 定义的全部 brush token。
        var appXaml = File.ReadAllText(Src("MemPilot.App", "App.xaml"));
        var toggle = Read("MemPilot.App", "MainWindow.xaml.cs");
        var idx = toggle.IndexOf("ToggleTheme_Click", StringComparison.Ordinal);
        var block = toggle[idx..];

        var defined = System.Text.RegularExpressions.Regex.Matches(appXaml,
                "SolidColorBrush x:Key=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        var swapped = System.Text.RegularExpressions.Regex.Matches(block,
                "Set(?:A)?\\(\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();

        var missing = defined.Except(swapped).Where(k => !k.StartsWith("Accent")).ToList();
        Assert.True(missing.Count == 0,
            "暗色主题换色表缺少这些 token（Accent 系主题中立可豁免）：" + string.Join(", ", missing));
    }
}
