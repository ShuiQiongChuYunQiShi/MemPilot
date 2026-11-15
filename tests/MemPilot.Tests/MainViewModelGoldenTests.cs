using MemPilot.Models;
using MemPilot.Services;
using MemPilot.ViewModels;
using System.Reflection;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// MainViewModel 拆分的 golden（v0.14.0，棱镜连续两轮第一热点：1052 行/87 分支）。
/// 按记忆库教训：纯重构必须先落 golden 再动刀——本文件锁住 VM 的**公开面**（属性/事件/方法签名）
/// 与关键行为语义，拆分为 partial 后一字不改全等才算行为零变更。拆完先跑这里，再跑全量。
/// </summary>
[Collection("Localization")]
public class MainViewModelGoldenTests : IDisposable
{
    private readonly string _dir;
    private readonly ConfigStore _config;
    private readonly MainViewModel _vm;

    public MainViewModelGoldenTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "gvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigStore(_dir);
        _vm = new MainViewModel(_config, new AuditLogger(_dir), new FeedbackStore(_dir));
    }

    public void Dispose()
    {
        try { _vm.Shutdown(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- 公开面：属性按字母序列出，拆分后必须一个不少（反射枚举，自动跟随）----

    [Fact]
    public void PublicSurface_Properties_AreLocked()
    {
        var props = _vm.GetType().GetProperties()
            .Select(p => $"{p.PropertyType.Name} {p.Name}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var expected = new List<string>
        {
            // 双轨状态（v0.11）
            "Boolean HasActionStatus", "String ActionStatus", "String StatusDisplay",
            // 长操作
            "Boolean LongOp", "String LongOpText",
            // 租约
            "String LeaseApiStatus", "String LeaseToken",
            // 提交告急 L3 非模态确认卡（v0.11）
            "Boolean CommitReliefPending",
            "Int64 CommitReliefReclaimMb",
            "String CommitReliefNames",
            "IReadOnlyList`1 CommitReliefCandidates",
        };
        var missing = expected.Except(props, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "拆分后丢失的公开属性：" + string.Join(", ", missing)
            + "（如属有意变更，必须同步修改本 golden 并在 CHANGELOG 说明）");
    }

    [Fact]
    public void PublicSurface_Methods_AreLocked()
    {
        var methods = _vm.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)   // 排除属性 getter/setter
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var expected = new[]
        {
            // 状态双轨
            "SetActionStatus", "RefreshStatus",
            // 租约
            "RestartLeaseApi",
            // L3 确认卡
            "ConfirmCommitRelief", "DismissCommitRelief",
            // 生命周期
            "StartEngine", "Shutdown", "RefreshAsync",
            // 页签感知
            "OnTabActivated", "LoadHistory", "ToggleCollapse",
        };
        var missing = expected.Where(m => !methods.Contains(m)).ToList();
        Assert.True(missing.Count == 0, "拆分后丢失的公开方法：" + string.Join(", ", missing));
    }

    [Fact]
    public void PublicSurface_Events_AreLocked()
    {
        var events = _vm.GetType().GetEvents().Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        // 注意：CommitReliefL3Suggested 在 ResidentEngine（Engine）上，不在 VM 上
        foreach (var e in new[] { "CollectionsRebuilt", "AuditChanged", "AlertRaised", "PropertyChanged" })
            Assert.True(events.Contains(e), $"拆分后丢失的公开事件：{e}");
    }

    // ---- 关键行为语义（拆分不得改变）----

    [Fact]
    public void StatusTwoTrack_ActionFeedbackSurvivesRefresh()
    {
        _vm.SetActionStatus("已标记用途：p1 → 工作");
        _vm.RefreshStatus("已枚举 438 个进程，0 个可清理候选（枚举 1ms · 分类 1ms）。");

        Assert.True(_vm.HasActionStatus);
        Assert.Equal("已标记用途：p1 → 工作", _vm.StatusDisplay);  // 动作反馈优先于刷新噪音
        Assert.Equal("已枚举 438 个进程，0 个可清理候选（枚举 1ms · 分类 1ms）。", _vm.Status);
    }

    [Fact]
    public void StatusTwoTrack_RefreshDoesNotEnterActionTrack()
    {
        _vm.RefreshStatus("刷新噪音文案");
        Assert.False(_vm.HasActionStatus, "RefreshStatus 不得进入 60 秒动作保留窗口");
        Assert.Equal("刷新噪音文案", _vm.StatusDisplay);
    }

    [Fact]
    public void LeaseStatus_ReportsListeningOrDisabled()
    {
        // RestartLeaseApi 在两种配置下都不抛、状态文案如实（端口冲突场景由 LeaseTests 覆盖）
        // v0.19.0：文案已资源化——无 UI 环境时 UiStrings 回退 key，状态机断言用 key。
        _config.Config.LeaseApiEnabled = false;
        _vm.RestartLeaseApi();
        Assert.Contains("Lease_StatusOff", _vm.LeaseApiStatus);

        _config.Config.LeaseApiEnabled = true;
        _vm.RestartLeaseApi();
        Assert.True(_vm.LeaseApiStatus.Contains("监听中") || _vm.LeaseApiStatus.Contains("未启动"),
            "状态必须是「监听中」或「未启动 + 原因」之一，实际：" + _vm.LeaseApiStatus);
        _config.Config.LeaseApiEnabled = false;
        _vm.RestartLeaseApi();   // 清理：关掉监听，避免残留端口占用影响其他测试
    }

    [Fact]
    public void DismissCommitRelief_LogsAndClears()
    {
        _vm.DismissCommitRelief();
        Assert.False(_vm.CommitReliefPending);
        Assert.Equal("", _vm.CommitReliefNames);
    }
}
