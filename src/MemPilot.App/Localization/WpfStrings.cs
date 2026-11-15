using System;
using System.Windows;
using MemPilot.Localization;

namespace MemPilot.App.Localization;

/// <summary>
/// WPF 侧的语言字典加载器（v0.19.0）。Core 只认 <see cref="UiStrings.Resolver"/>，
/// 这里负责把 ResourceDictionary 接上，并支持运行时切换（DynamicResource 自动刷新）。
/// </summary>
public static class WpfStrings
{
    private static ResourceDictionary? _current;

    /// <summary>当前生效的语言（zh-CN / en）。</summary>
    public static string Culture { get; private set; } = "zh-CN";

    /// <summary>语言切换后触发：code-behind 重建动态文案（状态栏/列表）用。</summary>
    public static event Action? Changed;

    /// <summary>auto → 跟随系统 UI 语言；其余映射到已支持的两套字典。</summary>
    public static string Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) ||
            language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var ui = System.Globalization.CultureInfo.CurrentUICulture;
            return ui.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-CN" : "en";
        }
        return language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh-CN";
    }

    /// <summary>加载并切换语言字典。必须在 Application 存在后调用（窗口创建前最佳）。</summary>
    public static void Apply(string? language)
    {
        var app = Application.Current ?? throw new InvalidOperationException("WpfStrings.Apply requires an Application");
        var culture = Resolve(language);
        var uri = new Uri($"pack://application:,,,/MemPilot;component/Resources/Strings.{culture}.xaml", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };

        if (_current != null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Add(dict);
        _current = dict;
        Culture = culture;

        UiStrings.Resolver = key => dict.Contains(key) ? dict[key] as string : null;
        Changed?.Invoke();
    }
}
