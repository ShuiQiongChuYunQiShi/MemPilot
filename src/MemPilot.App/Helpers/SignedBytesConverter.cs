using System.Globalization;
using System.Windows.Data;

namespace MemPilot.Helpers;

/// <summary>Signed byte sizes for growth columns ("+123.4 MB" / "-45.6 MB").</summary>
public class SignedBytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long b) return "";
        if (b == 0) return "0 MB";
        string sign = b > 0 ? "+" : "-";
        double v = Math.Abs(b);
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{sign}{v:F1} {units[i]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
