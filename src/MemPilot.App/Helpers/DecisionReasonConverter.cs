using System.Globalization;
using System.Windows.Data;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.Helpers;

/// <summary>
/// 决策日志 Reason 列（v0.22.0）：有 Code 的记录按当前语言渲染，旧记录回退中文原文。
/// 绑定源是整行 DecisionEntry。
/// </summary>
public sealed class DecisionReasonConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DecisionEntry e ? DecisionText.Render(e) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
