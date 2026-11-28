using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>一次 WSL 常驻提示（v0.22.0）。</summary>
public sealed class WslHint
{
    public ProcessInfo Process { get; init; } = new();
    public long TotalBytes { get; init; }
    public double TotalGb => TotalBytes / 1073741824.0;
    /// <summary>已运行小时数（用于文案）。</summary>
    public int RunningHours { get; init; }
}

/// <summary>
/// WSL 常驻提示（v0.22.0，默认开启）。
///
/// 为什么单独做：`vmmemWSL` 是 WSL2 / Hyper-V 的虚拟机内存宿主。
///  - 它不是垃圾：结束它会破坏 WSL（`JunkSafetyFilter` 虽然放行 ContainerVm，但语义上不该建议结束）；
///  - 它不能挂起：`NtSuspendProcess` 会让整个 WSL 停摆（`LongResidentAdvisor.IsSuspendUnsafe` 已排除）；
///  - 它也不该被压缩工作集（v0.22.0 起 `TrimSelection` / `UnusedDetector` / make-room 一致排除）。
/// 唯一正确的动作是 `wsl --shutdown`（关闭所有发行版，下次使用自动重启）。
///
/// 所以这里只做一件事：占用够大 + 今天零活动 + 已运行够久 → 交给 UI 提示，
/// **只提供复制命令，绝不自动执行**。实测本机峰值 10.4 GB、当前 1.6-4 GB。
/// </summary>
public static class WslHintAdvisor
{
    /// <summary>只匹配 WSL 的 vmmem（裸 vmmem 可能是 Hyper-V，`wsl --shutdown` 无效）。</summary>
    public static bool IsWslMemoryHost(string name)
        => name.Contains("vmmem", StringComparison.OrdinalIgnoreCase)
           && name.Contains("wsl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 计算一次提示。nowLocal 与 ProcessInfo.StartTime 同为本地时间轴。
    /// 返回 null 表示不值得提示（没在跑 / 占用不够 / 刚启动）。
    /// </summary>
    public static WslHint? Recommend(List<ProcessInfo> procs, long minBytes, int minAgeMinutes, DateTime nowLocal)
    {
        var p = procs.FirstOrDefault(x => IsWslMemoryHost(x.Name));
        if (p == null) return null;

        // vmmem 的工作集会被系统在空闲时回收，单看 WS 会漏报——取两者较大值
        long bytes = Math.Max(p.Memory.PrivateBytes, p.Memory.PrivateWorkingSetBytes);
        if (bytes < minBytes) return null;

        if (p.StartTime == DateTime.MinValue) return null;
        double hours = (nowLocal - p.StartTime).TotalHours;
        if (hours * 60 < minAgeMinutes) return null;

        return new WslHint { Process = p, TotalBytes = bytes, RunningHours = (int)hours };
    }
}
