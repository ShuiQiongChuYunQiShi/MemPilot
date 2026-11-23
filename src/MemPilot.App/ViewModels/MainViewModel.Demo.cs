using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

/// <summary>
/// 60 秒 commit 演示（v0.22.0）。
///
/// 唯一的差异化洞察是「清的物理内存和提交内存是两件事」，但它现在埋在 README 的
/// 数据集里。这里让用户点一下就能自己看到：回收闲置工作集后可用物理上涨，
/// **提交余量几乎不动**——这不是失败，是预期，因为降提交只能靠结束闲置进程。
///
/// 复用现有 `TrimAllWorkingSetsAsync`（安全子集 + 10 分钟冷却），不新增危险动作。
/// </summary>
public partial class MainViewModel
{
    public sealed record CommitDemoResult(
        long AvailBefore, long AvailAfter,
        long CommitFreeBefore, long CommitFreeAfter,
        int Trimmed, int Failed);

    private CommitDemoResult? _demo;

    private bool _demoPending;
    public bool DemoPending
    {
        get => _demoPending;
        private set => SetUi(() => _demoPending = value);
    }

    public string DemoPhysText => _demo == null
        ? ""
        : UiStrings.F("Demo_PhysLine", Gb(_demo.AvailBefore), Gb(_demo.AvailAfter), Delta(_demo.AvailAfter - _demo.AvailBefore));

    public string DemoCommitText => _demo == null
        ? ""
        : UiStrings.F("Demo_CommitLine", Gb(_demo.CommitFreeBefore), Gb(_demo.CommitFreeAfter), Delta(_demo.CommitFreeAfter - _demo.CommitFreeBefore));

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("F1");

    private static string Delta(long bytes) => (bytes >= 0 ? "+" : "") + (bytes / 1073741824.0).ToString("F1");

    /// <summary>执行演示：快照 → 安全子集 L1 → 快照 → 结果卡。</summary>
    public async Task RunCommitDemoAsync()
    {
        var before = ProcessService.GetSystemMemory();
        var summary = await TrimAllWorkingSetsAsync().ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);   // 等系统内存统计稳定
        var after = ProcessService.GetSystemMemory();

        // TrimAllWorkingSetsAsync 的文案里带 "已压缩 N 个 / 跳过 M 个"，这里只需要数字
        int trimmed = 0, failed = 0;
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+)[^\d]+(\d+)");
            if (m.Success)
            {
                trimmed = int.Parse(m.Groups[1].Value);
                failed = int.Parse(m.Groups[2].Value);
            }
        }
        catch { }

        _demo = new CommitDemoResult(
            before.AvailablePhysicalBytes, after.AvailablePhysicalBytes,
            before.TotalCommitLimitBytes - before.TotalCommittedBytes,
            after.TotalCommitLimitBytes - after.TotalCommittedBytes,
            trimmed, failed);
        OnPropertyChanged(nameof(DemoPhysText));
        OnPropertyChanged(nameof(DemoCommitText));
        DemoPending = true;
    }

    public void CloseCommitDemo()
    {
        DemoPending = false;
        _demo = null;
        OnPropertyChanged(nameof(DemoPhysText));
        OnPropertyChanged(nameof(DemoCommitText));
    }
}
