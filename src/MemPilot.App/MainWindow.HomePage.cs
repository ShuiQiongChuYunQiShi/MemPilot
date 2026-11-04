using System.Diagnostics;
using System.IO;

namespace MemPilot.App;

/// <summary>
/// 首次运行说明（v0.10.0）：主页顶部一条可关闭的横幅，说明"默认什么都不会做"。
/// 为什么不做模态引导：用户明确反感模态弹窗（只用于破坏性确认），而这条信息本身
/// 只需要"能随时回头看"——所以做成横幅 + 两个链接，关掉后写 config 永久不再出现。
/// </summary>
public partial class MainWindow : Window
{
    private void ApplyFirstRunNotice()
    {
        if (FirstRunNotice == null) return;
        FirstRunNotice.Visibility = _config.Config.FirstRunNoticeDismissed
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DismissFirstRunNotice_Click(object sender, RoutedEventArgs e)
    {
        _config.Config.FirstRunNoticeDismissed = true;
        try { _config.Save(); } catch (Exception ex) { Diag.Record("firstrun:save", ex); }
        if (FirstRunNotice != null) FirstRunNotice.Visibility = Visibility.Collapsed;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_config.DataDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { _vm.Status = "打开数据目录失败：" + ex.Message; }
    }

    private void OpenReadme_Click(object sender, RoutedEventArgs e)
    {
        // 发布包里 README 就在 exe 旁边；开发态则向上找仓库根。
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "README.md");
                if (File.Exists(candidate))
                {
                    Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                    return;
                }
            }
            _vm.Status = "没找到 README.md——可在仓库页面查看。";
        }
        catch (Exception ex) { _vm.Status = "打开说明失败：" + ex.Message; }
    }
}
