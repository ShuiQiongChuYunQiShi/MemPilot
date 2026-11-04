using System.Globalization;
using System.Windows.Data;
using MemPilot.Services;

namespace MemPilot.Helpers;

/// <summary>
/// 决策日志 Kind / 审计 Action 的中文映射（v0.11.0 P2-4）。
/// 摸排结论：这两页把内部枚举值（GrowthWatch / L1Trim / CommitRelief…）直接亮给用户，
/// 是"最像开发者表"的两页。映射表集中在 UiTerms 旁边，加新 Kind 时一处补齐。
/// </summary>
public class KindTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => UiTerms.OfKind(value as string ?? "");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
