using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>场景识别信号快照（v0.2.2）。GPU 以 -1 表示不可用，判定层不得把它当 0 分。</summary>
public class SceneSignals
{
    public bool ForegroundFullscreen;
    public string ForegroundName = "";
    public string ForegroundTitle = "";
    public ProcessCategory ForegroundCategory = ProcessCategory.Unknown;
    public double ForegroundGpu = -1;      // 前台进程 GPU%（-1 不可用）
    public bool AnyAntiCheat;
    public bool GameProcessBusy;           // 游戏类进程 CPU>5 或 GPU>10
    public bool AiProcessResident;         // AI 类进程 PrivateBytes>512MB
    public double AiGpu = -1;              // AI 类进程最大 GPU%
    public bool AiBusy;                    // AI 类 CPU 总和>30% 或 AiGpu>20
    public bool AudioActive;
    public double InputIdleSeconds;
    public double RecentInputRatio;        // 最近 2 分钟内"有输入"采样占比 0..1
    public bool DevForeground;
    public bool DevCompileActive;          // 开发类进程 CPU>10 或 GPU>5
}

public class SceneDecision
{
    public AppMode? Target { get; set; }   // null = 不切换（置信不足或信号不足）
    public double GameScore { get; set; }  // 娱乐（游戏/直播/影视）
    public double AiScore { get; set; }
    public double DevScore { get; set; }
    public bool Decisive { get; set; }
    public string Reason { get; set; } = "";
    public List<string> Signals { get; } = new();

    public string Summary =>
        $"娱乐 {GameScore:F0} / AI {AiScore:F0} / 开发 {DevScore:F0}" +
        (Target.HasValue ? $" → {Target}" : " → 暂不切换") +
        (Signals.Count > 0 ? $"（{string.Join("，", Signals.Take(5))}）" : "");
}

/// <summary>
/// 场景识别 v2（v0.2.2）：多信号加权打分，替代"单信号即切换"的旧规则。
/// 设计原则：
///  - 场景按活动类别划分：看直播 = 玩游戏 = 娱乐（用户裁决），不看具体应用；
///  - 任何单信号都不能决定切换（反作弊常驻≠在玩、模型常驻≠在推）；
///  - 结果必须"分数达标 + 领先次高"才 Decisive，否则只建议（影子模式记录）；
///  - GPU 不可用时按其余信号判定（不把 -1 当 0 分）。
/// </summary>
public static class SceneDetector
{
    public const double Threshold = 60;
    public const double Margin = 20;

    private static readonly string[] GameTitleWords =
        { "逆战", "三角洲", "英雄联盟", "原神", "崩坏", "永劫", "Steam", "Wallpaper Engine", "游戏" };

    private static readonly string[] DevTitleWords =
        { "Visual Studio", "VS Code", "Rider", "IDEA", "PyCharm", "CLion", "Terminal", "PowerShell", "Git Bash", "Jupyter", "Docker", "MySQL" };

    public static SceneDecision Evaluate(SceneSignals s)
    {
        double game = 0, ai = 0, dev = 0;
        var sig = new List<string>();
        bool inputActive = s.InputIdleSeconds < 5 && s.RecentInputRatio >= 0.5;

        // ---- 娱乐（游戏 / 直播 / 影视） ----
        if (s.ForegroundFullscreen) { game += 30; sig.Add("全屏"); }
        if (s.ForegroundGpu >= 20) { game += 20; sig.Add($"前台GPU {s.ForegroundGpu:F0}%"); }
        else if (s.ForegroundGpu >= 5) { game += 10; sig.Add($"前台GPU {s.ForegroundGpu:F0}%"); }
        if (inputActive) { game += 15; sig.Add("输入活跃"); }
        else if (s.RecentInputRatio >= 0.2) game += 5;
        if (s.AnyAntiCheat) { game += 15; sig.Add("反作弊在场"); }
        if (s.GameProcessBusy) { game += 15; sig.Add("游戏进程活跃"); }
        if (s.ForegroundCategory == ProcessCategory.Game) { game += 15; sig.Add("前台是游戏"); }
        if (s.AudioActive) game += 5;
        if (HasAny(s.ForegroundTitle, GameTitleWords)) { game += 10; sig.Add("标题像游戏"); }
        // 全屏影音：全屏 + 有声 + 长时间无输入 → 看直播/影视也归娱乐
        if (s.ForegroundFullscreen && s.AudioActive && s.InputIdleSeconds > 30)
        { game += 15; sig.Add("全屏影音（长时间无输入）"); }

        // ---- AI 生成 ----
        if (s.AiProcessResident) { ai += 25; sig.Add("AI 工具常驻"); }
        if (s.AiGpu >= 20) { ai += 30; sig.Add($"AI GPU {s.AiGpu:F0}%"); }
        else if (s.AiGpu >= 5) ai += 10;
        if (s.AiBusy) { ai += 20; sig.Add("AI 负载活跃"); }
        if (s.InputIdleSeconds > 60) { ai += 10; sig.Add("长时间无输入（批处理特征）"); }
        if (s.ForegroundCategory == ProcessCategory.AiImageVideo) ai += 10;

        // ---- 开发 ----
        if (s.DevForeground) { dev += 30; sig.Add("前台是开发工具"); }
        if (inputActive) { dev += 15; }
        if (s.DevCompileActive) { dev += 20; sig.Add("编译/数据库负载"); }
        if (HasAny(s.ForegroundTitle, DevTitleWords)) { dev += 10; sig.Add("标题像开发工具"); }

        // ---- 选择：达标 + 领先 ----
        var best = Math.Max(game, Math.Max(ai, dev));
        var second = new[] { game, ai, dev }.OrderByDescending(x => x).ElementAt(1);
        var decision = new SceneDecision { GameScore = game, AiScore = ai, DevScore = dev };
        decision.Signals.AddRange(sig);

        if (best < Threshold || best - second < Margin)
        {
            decision.Decisive = false;
            decision.Reason = best < Threshold
                ? $"最高分 {best:F0} < {Threshold}（信号不足以切换）"
                : $"领先差距 {best - second:F0} < {Margin}（场景不明确）";
            return decision;
        }

        decision.Decisive = true;
        decision.Target = game == best ? AppMode.Game
            : ai == best ? AppMode.AiGeneration
            : AppMode.Development;
        decision.Reason = $"最高分 {best:F0}，领先次高 {best - second:F0}";
        return decision;
    }

    private static bool HasAny(string text, string[] words)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var w in words)
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
