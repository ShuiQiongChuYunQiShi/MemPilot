namespace MemPilot.Localization;

/// <summary>
/// 界面文案解析入口（v0.19.0 i18n）。
///
/// 为什么不用 .resx：项目已全面用 DynamicResource 做主题令牌，文案沿用同一个
/// ResourceDictionary 机制可以**运行时切语言**（无需重启），也与现有 XAML 风格一致。
/// Core 保持零 UI 依赖：这里只持有 App 注入的解析器；未注入时（单测/headless）
/// 返回 key 本身，便于降级与断言。
/// </summary>
public static class UiStrings
{
    /// <summary>由 App 启动时注入：key → 当前语言的文案；找不到返回 null。</summary>
    public static Func<string, string?>? Resolver { get; set; }

    /// <summary>取一条文案；未命中先回退 key，并记一次 Diag（同 key 只记一次）。</summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (Resolver != null)
        {
            var value = Resolver(key);
            if (value != null) return value;
            ReportMissing(key);
        }
        return key;
    }

    /// <summary>带参数文案：模板用 {0}/{1}，翻译时保持占位符顺序。</summary>
    public static string F(string key, params object?[] args)
    {
        var template = Get(key);
        if (args.Length == 0) return template;
        try { return string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>命中返回 true；未命中不记 Diag（供"未知枚举回退原值"的场景）。</summary>
    public static bool TryGet(string key, out string value)
    {
        var resolved = Resolver?.Invoke(key);
        if (resolved != null) { value = resolved; return true; }
        value = key;
        return false;
    }

    private static readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private static void ReportMissing(string key)
    {
        lock (_reported)
        {
            if (!_reported.Add(key) || _reported.Count > 50) return;
        }
        Helpers.Diag.Record("i18n:missing", key);
    }

    /// <summary>测试用：清空注入与去重状态。</summary>
    public static void ResetForTests()
    {
        Resolver = null;
        lock (_reported) _reported.Clear();
    }
}
