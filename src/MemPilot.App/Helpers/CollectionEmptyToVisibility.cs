using System.Collections;
using System.Globalization;
using System.Windows.Data;

namespace MemPilot.Helpers;

/// <summary>
/// 空状态占位（v0.11.0 P1-4）：集合为空时显示引导文案的覆盖层。
/// 摸排结论：8 个 DataGrid 冷启动全是空白——用户不知道"没数据"是正常还是坏了。
/// </summary>
public class CollectionEmptyToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ICollection c && c.Count > 0
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>反向：集合非空时 Visible（占位层用 Empty，内容用这个不需要——直接绑即可）。</summary>
public class CollectionNotEmptyToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ICollection c && c.Count > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
