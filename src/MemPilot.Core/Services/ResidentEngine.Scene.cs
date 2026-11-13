using System.Diagnostics;
using System.Runtime.InteropServices;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>场景识别与模式切换（v0.3.9 从 ResidentEngine god-class 拆出）：打分、影子记录、滞回切换、切换时的挂起恢复。</summary>
public partial class ResidentEngine
{

    // ---- mode switching with hysteresis ----

    // ---- 场景识别 v2（v0.2.2）：多信号打分 + 影子模式 + 自动切换 ----

    private void EvaluateScene(List<ProcessInfo> procs)
    {
        try
        {
            var gpuMap = _gpuUsage.Sample();
            LastGpuPercent = gpuMap; // v0.3.4：供显存采样器记录逐进程 GPU%（复用同一次 PDH 采集）
            var now = DateTime.UtcNow;
            double idle = InputActivity.IdleSeconds();
            _inputSamples.Add((now, idle));
            _inputSamples.RemoveAll(x => (now - x.utc).TotalSeconds > 120);
            double inputRatio = _inputSamples.Count == 0 ? 0
                : (double)_inputSamples.Count(x => x.idle < 5) / _inputSamples.Count;

            var fg = procs.FirstOrDefault(p => p.Pid == _lastForegroundPid);
            double FgGpu(int pid) => gpuMap.TryGetValue(pid, out var g) ? g : -1;
            double aiGpu = procs.Where(p => p.Category == ProcessCategory.AiImageVideo)
                                .Select(p => FgGpu(p.Pid)).DefaultIfEmpty(-1).Max();
            double aiCpu = procs.Where(p => p.Category == ProcessCategory.AiImageVideo)
                                .Sum(p => p.Memory.CpuPercent);

            var signals = new SceneSignals
            {
                ForegroundFullscreen = ForegroundFullscreen,
                ForegroundName = fg?.Name ?? ForegroundProcessName,
                ForegroundTitle = fg?.MainWindowTitle ?? "",
                ForegroundCategory = fg?.Category ?? ProcessCategory.Unknown,
                ForegroundGpu = fg != null ? FgGpu(fg.Pid) : -1,
                AnyAntiCheat = procs.Any(p => p.Category == ProcessCategory.AntiCheat),
                GameProcessBusy = procs.Any(p => p.Category == ProcessCategory.Game &&
                    (p.Memory.CpuPercent > 5 || FgGpu(p.Pid) > 10)),
                AiProcessResident = procs.Any(p => p.Category == ProcessCategory.AiImageVideo &&
                    p.Memory.PrivateBytes > 512L * 1024 * 1024),
                AiGpu = aiGpu,
                AiBusy = aiCpu > 30 || aiGpu > 20,
                AudioActive = procs.Any(p => p.AudioActive),
                InputIdleSeconds = idle,
                RecentInputRatio = inputRatio,
                DevForeground = fg?.Category == ProcessCategory.DevTool,
                DevCompileActive = procs.Any(p => p.Category == ProcessCategory.DevTool &&
                    (p.Memory.CpuPercent > 10 || FgGpu(p.Pid) > 5)),
            };

            var decision = SceneDetector.Evaluate(signals);
            LastSceneDecision = decision;

            var cfg = _config.Config;
            if (cfg.AutoModeSwitch)
            {
                ApplySceneDecision(decision);
            }
            else if (cfg.SceneShadowEnabled && decision.Decisive && decision.Target.HasValue)
            {
                // 影子模式：只记录"如果自动切换，会切到哪"，绝不动作；目标变化且距上次 ≥5 分钟才记
                if (decision.Target != _lastShadowTarget && (now - _lastShadowLogUtc).TotalSeconds >= 300)
                {
                    _lastShadowTarget = decision.Target;
                    _lastShadowLogUtc = now;
                    _decisions.Log(new DecisionEntry
                    {
                        Kind = "SceneShadow",
                        Target = decision.Target.Value.ToString(),
                        Decision = "如果自动切换：会切",
                        Reason = decision.Reason + "；信号：" + string.Join("、", decision.Signals.Take(6))
                    });
                }
            }
        }
        catch (Exception ex) { Diag.Record("scene", ex); } // v0.3.9：场景评估失败不再无声
    }

    private void ApplySceneDecision(SceneDecision decision)
    {
        var cfg = _config.Config;
        if (!decision.Decisive || !decision.Target.HasValue)
        {
            _pendingMode = null;
            _modeStreak = 0;
            return;
        }
        var target = decision.Target.Value;
        if (target == cfg.CurrentMode)
        {
            _pendingMode = null;
            _modeStreak = 0;
            return;
        }
        if (_pendingMode == target) _modeStreak++;
        else { _pendingMode = target; _modeStreak = 1; }
        if (_modeStreak < 2 || (DateTime.UtcNow - _lastModeSwitchUtc).TotalSeconds < 60) return;

        var from = cfg.CurrentMode;
        cfg.CurrentMode = target;
        _config.Save();
        _lastModeSwitchUtc = DateTime.UtcNow;
        _pendingMode = null;
        _modeStreak = 0;
        _decisions.Log(new DecisionEntry
        {
            Kind = "ModeSwitch",
            Target = $"{from} → {target}",
            Decision = "自动切换",
            Reason = $"{decision.Reason}（滞回确认 2 轮）"
        });
        MaybeResumeSuspendedOnModeChange();
        ModeChanged?.Invoke(this, new ModeSwitchEventArgs { Mode = target, Reason = decision.Reason });
    }

    public event EventHandler<ModeSwitchEventArgs>? ModeChanged;

    private void MaybeResumeSuspendedOnModeChange()
    {
        if (!_config.Config.ResumeSuspendedOnModeChange) return;
        var records = _cleanup.Suspended.LiveRecords();
        foreach (var rec in records)
            _cleanup.ResumeRecord(rec);   // v0.18.0：走 (pid,startTime) 校验，不按裸 PID 恢复
        if (records.Count > 0)
            _decisions.Log(new DecisionEntry
            {
                Kind = "AutoCleanup",
                Target = $"{records.Count} 个挂起进程",
                Decision = "恢复",
                Reason = "模式切换且开启了“切模式自动恢复挂起”"
            });
    }
}
