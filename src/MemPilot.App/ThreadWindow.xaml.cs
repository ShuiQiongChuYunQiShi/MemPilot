using System.IO;
using System.Windows;
using System.Windows.Controls;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.App;

/// <summary>
/// 线程管理小窗口（v0.6.0）：原「🧵 线程」独立页降级而来——TID 级挂起/恢复是冷门的
/// 电源用户功能，不值得占一个导航页。只展示选中进程的线程，关窗即释放。
/// </summary>
public partial class ThreadWindow : Window
{
    private readonly ProcessInfo _process;
    private readonly ProcessService _svc;

    public ThreadWindow(ProcessInfo process, ProcessService svc)
    {
        InitializeComponent();
        _process = process;
        _svc = svc;
        HeaderText.Text = $"线程 — {process.Name} (PID {process.Pid})";
        Title = $"线程 — {process.Name}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var list = await Task.Run(() =>
            _svc.EnumerateAllThreads(new[] { _process })
                .Where(t => t.OwnerPid == _process.Pid)
                .ToList());
        ThreadGrid.ItemsSource = list;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Suspend_Click(object sender, RoutedEventArgs e) => await Act(suspend: true);
    private async void Resume_Click(object sender, RoutedEventArgs e) => await Act(suspend: false);

    private async Task Act(bool suspend)
    {
        if (ThreadGrid.SelectedItem is not ThreadInfo ti) return;
        try
        {
            if (suspend) _svc.SuspendThread(ti.Tid);
            else _svc.ResumeThread(ti.Tid);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{(suspend ? "挂起" : "恢复")}线程 {ti.Tid} 失败：{ex.Message}", "线程",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
