using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>场景识别 v2：任何单信号都不能决定切换；看直播=玩游戏=娱乐。</summary>
public class SceneDetectorTests
{
    private static SceneSignals Base() => new()
    {
        InputIdleSeconds = 120,
        RecentInputRatio = 0,
    };

    [Fact]
    public void NoSignals_IsNotDecisive()
    {
        var d = SceneDetector.Evaluate(Base());
        Assert.Null(d.Target);
        Assert.False(d.Decisive);
    }

    // 旧规则误报场景：反作弊常驻但没在玩（无全屏、无输入、无 GPU）
    [Fact]
    public void IdleAntiCheatAlone_DoesNotSwitchToEntertainment()
    {
        var s = Base();
        s.AnyAntiCheat = true;
        var d = SceneDetector.Evaluate(s);
        Assert.False(d.Decisive);
        Assert.Null(d.Target);
    }

    // 窗口化游戏：GPU 高 + 输入活跃 + 游戏进程活跃 + 前台是游戏
    [Fact]
    public void WindowedGame_WithGpuAndInput_IsEntertainment()
    {
        var s = Base();
        s.ForegroundCategory = ProcessCategory.Game;
        s.ForegroundGpu = 45;
        s.InputIdleSeconds = 1;
        s.RecentInputRatio = 0.9;
        s.GameProcessBusy = true;
        s.AudioActive = true;
        var d = SceneDetector.Evaluate(s);
        Assert.True(d.Decisive);
        Assert.Equal(AppMode.Game, d.Target);
    }

    // 看直播/影视：全屏 + 有声 + 长时间无输入 → 娱乐（与玩游戏同一场景）
    [Fact]
    public void FullscreenStreaming_IsEntertainment()
    {
        var s = Base();
        s.ForegroundFullscreen = true;
        s.ForegroundCategory = ProcessCategory.ThirdPartyApp;
        s.ForegroundGpu = 12;
        s.ForegroundTitle = "哔哩哔哩 直播";
        s.AudioActive = true;
        s.InputIdleSeconds = 300;
        var d = SceneDetector.Evaluate(s);
        Assert.True(d.Decisive);
        Assert.Equal(AppMode.Game, d.Target);
    }

    // ollama 挂着模型但没在推理：常驻信号单独不足以判 AI
    [Fact]
    public void IdleAiModelResident_DoesNotSwitchToAi()
    {
        var s = Base();
        s.AiProcessResident = true;
        s.AiGpu = 0;
        s.InputIdleSeconds = 2;
        s.RecentInputRatio = 0.8;
        var d = SceneDetector.Evaluate(s);
        Assert.False(d.Decisive);
    }

    [Fact]
    public void BusyAiInference_IsAiMode()
    {
        var s = Base();
        s.AiProcessResident = true;
        s.AiGpu = 55;
        s.AiBusy = true;
        s.InputIdleSeconds = 200;
        var d = SceneDetector.Evaluate(s);
        Assert.True(d.Decisive);
        Assert.Equal(AppMode.AiGeneration, d.Target);
    }

    // 无界面开发：前台 IDE + 输入活跃 + 编译/DB 负载
    [Fact]
    public void DevForegroundWithInputAndCompile_IsDevMode()
    {
        var s = Base();
        s.ForegroundCategory = ProcessCategory.DevTool;
        s.ForegroundTitle = "MemPilot - Visual Studio";
        s.InputIdleSeconds = 2;
        s.RecentInputRatio = 0.8;
        s.DevForeground = true;
        s.DevCompileActive = true;
        var d = SceneDetector.Evaluate(s);
        Assert.True(d.Decisive);
        Assert.Equal(AppMode.Development, d.Target);
    }

    // 场景不明确：两个场景接近时不切（领先差距不足）
    [Fact]
    public void AmbiguousScenes_DoNotSwitch()
    {
        var s = Base();
        s.ForegroundCategory = ProcessCategory.DevTool;
        s.InputIdleSeconds = 1;
        s.RecentInputRatio = 0.9;
        s.DevForeground = true;          // dev 45
        s.ForegroundFullscreen = true;   // 娱乐 30 + 输入 15 = 45
        var d = SceneDetector.Evaluate(s);
        Assert.False(d.Decisive);
    }
}
