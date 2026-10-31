namespace MemPilot.Services;

/// <summary>侧栏页签（键 = 页签头里的 emoji，与 MainWindow 的 TabControl 一一对应）。</summary>
public enum TabPage
{
    Home,
    Process,
    Cleanup,
    Suspended,
    Decisions,
    Other,
}

/// <summary>
/// 页签重建策略（v0.3.3，棱镜 test/ui-regression）。
///
/// 背景：v0.1.6 修过「切页签卡 4-5 秒」——根因是每个刷新周期都全量重建 19 列 × ~550 行的进程集合。
/// 修法是「只重建当前可见页签 + 4 秒节流」，但这段判断原先埋在 MainViewModel 的 switch 里，
/// 没有测试守着，重构时很容易悄悄退回「每 tick 全量重建」，而用户只能靠手感发现。
///
/// 这里把「哪些页签需要重建什么」抽成纯函数：ViewModel 必须经它判断，测试直接锁语义。
/// </summary>
public static class TabRebuildPolicy
{
    /// <summary>可见页签的集合重建最短间隔（秒）。切页签时由 OnTabActivated 强制重建，不走这个节流。</summary>
    public const double MinSecondsBetweenRebuilds = 4;

    public static TabPage Parse(string? key) => key switch
    {
        "📋" => TabPage.Process,
        "🧹" => TabPage.Cleanup,
        "⏸" => TabPage.Suspended,
        "🧠" => TabPage.Decisions,
        "🏠" => TabPage.Home,
        _ => TabPage.Other,
    };

    /// <summary>只有进程页需要全量重建进程集合（排序 + 分组 + 过滤，最贵的一条路径）。</summary>
    public static bool RebuildsProcessCollection(TabPage page) => page == TabPage.Process;

    /// <summary>刷新节流：距上次重建不足 <see cref="MinSecondsBetweenRebuilds"/> 秒就跳过这次 UI 重建。</summary>
    public static bool ShouldRebuild(DateTime lastRebuildUtc, DateTime nowUtc, double minSeconds = MinSecondsBetweenRebuilds)
        => (nowUtc - lastRebuildUtc).TotalSeconds >= minSeconds;
}
