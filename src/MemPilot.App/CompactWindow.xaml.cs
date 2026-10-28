using System.Windows;
using System.Windows.Input;
using MemPilot.Models;
using MemPilot.Services;
using MemPilot.ViewModels;

namespace MemPilot.App;

/// <summary>
/// Compact always-on-top mini bar (WMC-style): memory gauge plus the two most-used
/// actions. Shares the MainViewModel — lightweight mode keeps it fed with fresh
/// SysMem while the main window is hidden.
/// </summary>
public partial class CompactWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ConfigStore _config;
    private readonly Action _openMainMakeRoom;
    private readonly Action _showMain;

    public CompactWindow(MainViewModel vm, ConfigStore config, Action openMainMakeRoom, Action showMain)
    {
        InitializeComponent();
        _vm = vm;
        _config = config;
        _openMainMakeRoom = openMainMakeRoom;
        _showMain = showMain;
        DataContext = _vm;

        if (_config.Config.CompactLeft >= 0 && _config.Config.CompactTop >= 0)
        {
            Left = _config.Config.CompactLeft;
            Top = _config.Config.CompactTop;
        }
        else
        {
            // bottom-right of the work area by default
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Bottom - Height - 24;
        }
    }

    private void Compact_Drag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
            _config.Config.CompactLeft = Left;
            _config.Config.CompactTop = Top;
            _config.Save();
        }
        catch { }
    }

    private async void CompactOptimize_Click(object sender, RoutedEventArgs e)
    {
        var targets = _vm.UnusedCandidates.ToList();
        if (targets.Count == 0)
        {
            CompactStatus.Text = "没有候选（观察期累积中）";
            return;
        }
        foreach (var c in targets)
            await _vm.PerformCleanupAsync(c, CleanupActionLevel.L1Trim);
        CompactStatus.Text = $"已回收 {targets.Count} 项";
    }

    private void CompactMakeRoom_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openMainMakeRoom();
    }

    private void CompactMain_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _showMain();
    }

    private void CompactClose_Click(object sender, RoutedEventArgs e) => Close();
}
