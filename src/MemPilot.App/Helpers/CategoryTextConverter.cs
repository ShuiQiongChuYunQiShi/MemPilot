using System.Globalization;
using System.Windows.Data;
using MemPilot.Models;

namespace MemPilot.Helpers;

/// <summary>Category enum → short Chinese label for dense grid columns.</summary>
public class CategoryTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ProcessCategory c ? c switch
        {
            ProcessCategory.SystemCritical => "系统关键",
            ProcessCategory.SystemService => "系统",
            ProcessCategory.Driver => "驱动",
            ProcessCategory.SecuritySoftware => "安全",
            ProcessCategory.AntiCheat => "反作弊",
            ProcessCategory.Game => "游戏",
            ProcessCategory.GameLauncher => "游戏平台",
            ProcessCategory.DevTool => "开发",
            ProcessCategory.AiImageVideo => "AI",
            ProcessCategory.ContainerVm => "容器",
            ProcessCategory.BackgroundService => "后台",
            ProcessCategory.Bloatware => "垃圾",
            ProcessCategory.ThirdPartyApp => "三方",
            _ => "未识别"
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
