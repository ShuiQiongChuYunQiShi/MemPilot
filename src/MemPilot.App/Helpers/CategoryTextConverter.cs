using System.Globalization;
using System.Windows.Data;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.Helpers;

/// <summary>Category enum → 短标签（密集网格列）。v0.19.0：文案来自字典，切语言即时生效。</summary>
public class CategoryTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ProcessCategory c ? UiTerms.OfCategory(c, shortForm: true) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
