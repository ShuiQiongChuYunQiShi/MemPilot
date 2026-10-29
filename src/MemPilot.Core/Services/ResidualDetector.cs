namespace MemPilot.Services;

/// <summary>
/// 孤儿残留检测：后台组件在宿主程序退出后仍驻留。实证（2026-10-29 遥测）：QQ 影音关闭后
/// QQLiveService 以 110–127MB 全程在场；wetype_update / lghub_updater / promecefpluginhost
/// 也长期常驻。映射只收本机实测确认的家族，宁少勿滥。
/// 仅提供提示理由，不做任何处置——Windows 服务仍被 IsService 排除，永远进不了自动候选。
/// </summary>
public static class ResidualDetector
{
    // 组件进程名 → 宿主进程名（任一生还即视为正常）
    private static readonly Dictionary<string, string[]> HostMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qqliveservice"] = new[] { "qqlive" },
        ["qqlivelcservice"] = new[] { "qqlive" },
        ["wetype_update"] = new[] { "wetype_server", "wetype_service", "wetype_renderer" },
        ["lghub_updater"] = new[] { "lghub", "lghub_agent" },
        ["promecefpluginhost"] = new[] { "wps", "wpspdf", "et", "wpp" },
    };

    /// <summary>返回缺失宿主的名字（斜杠连接）当该进程是已知后台组件且宿主全部退出；否则 null。</summary>
    public static string? FindOrphanHost(string processName, ISet<string> runningNames)
    {
        if (!HostMap.TryGetValue(processName, out var hosts)) return null;
        foreach (var h in hosts)
            if (runningNames.Contains(h)) return null;
        return string.Join("/", hosts);
    }
}
