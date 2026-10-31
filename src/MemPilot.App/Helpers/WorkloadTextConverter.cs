using System.Globalization;
using System.Windows.Data;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.Helpers;

/// <summary>WorkloadKind → 中文短标签（进程页"用途"列）。</summary>
public class WorkloadTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WorkloadKind k ? WorkloadClassifier.DisplayName(k) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
