using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

public partial class MainViewModel
{
    // ---- make-room workflow ----

    public async Task<MakeRoomPlan> BuildMakeRoomPlanAsync(MakeRoomRequest req)
    {
        if (_lastProcs.Count == 0) await RefreshAsync().ConfigureAwait(true);
        Engine.SampleForeground();
        var audio = AudioService.GetActiveAudioPids();
        var mem = SysMem.TotalPhysicalBytes == 0 ? ProcessService.GetSystemMemory() : SysMem;
        var profile = ConfigStore.Config.ModeProfiles.TryGetValue(ConfigStore.Config.CurrentMode, out var pr)
            ? pr
            : new ModeProfile { Mode = ConfigStore.Config.CurrentMode, Name = ConfigStore.Config.CurrentMode.ToString() };
        // 历史权重（近 7 天出镜率 × 峰值 P90）：只影响排序，不动资格
        var weights = UsageAnalytics.ComputeProcessWeights(Usage.ReadWindow(TimeSpan.FromDays(7)));
        var plan = Cleanup.BuildPlan(_lastProcs, mem, profile, req, Engine.LastForegroundPid, audio, weights);
        ReloadAudit();
        return plan;
    }

    public async Task<MakeRoomResult> ExecuteMakeRoomAsync(MakeRoomPlan plan, CancellationToken ct)
    {
        var result = await Task.Run(() => Cleanup.ExecutePlanAsync(plan, ConfigStore.Config.GracefulCloseTimeoutSeconds, ct)).ConfigureAwait(false);
        ReloadAudit();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => SysMem = ProcessService.GetSystemMemory());
        return result;
    }
}
