using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MemPilot.Helpers;

/// <summary>Tree depth (int) → indent width (px).</summary>
public class IndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int depth && depth > 0 ? depth * 16d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Collapsed state (bool) → tree glyph: true = collapsed "▸", false = expanded "▾".</summary>
public class CollapsedGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▸" : "▾";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
