using System.Windows.Input;

namespace MemPilot.App;

/// <summary>
/// 全局键盘支持（v0.11.0 P1-4，UX 摸排）。
/// 摸排结论：全应用此前零 KeyBinding、零 Esc 处理——鼠标-only 的工具对键盘用户不友好，
/// 且 Esc 无法取消腾内存执行是最被诟病的一类缺失。绑定集中在构造函数里注册（见 MainWindow ctor）。
/// </summary>
public partial class MainWindow
{
    /// <summary>注册窗口级快捷键。在 InitializeComponent 之后调用一次。</summary>
    private void RegisterKeyBindings()
    {
        InputBindings.Add(new KeyBinding(new RelayCommand(() =>
        {
            // v0.22.0：焦点在搜索框时优先清搜索（用户按 Esc 的意图由焦点决定）；
            // 否则 Esc 取消腾内存执行。
            if (SearchBox != null && SearchBox.IsFocused && SearchBox.Text.Length > 0)
                SearchBox.Clear();
            else if (_planCts != null) _planCts.Cancel();
        }), new KeyGesture(Key.Escape)));

        InputBindings.Add(new KeyBinding(new RelayCommand(() => _ = _vm.RefreshAsync()),
            new KeyGesture(Key.F5)));

        InputBindings.Add(new KeyBinding(new RelayCommand(() =>
        {
            SwitchToTab("process");
            SearchBox?.Focus();
        }), new KeyGesture(Key.F, ModifierKeys.Control)));

        // 腾内存页 Enter 直接生成计划（输入框内）
        if (TargetAvailGB != null)
            TargetAvailGB.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { PlanMakeRoom_Click(TargetAvailGB, new RoutedEventArgs()); e.Handled = true; }
            };
    }

    /// <summary>极简 ICommand（WPF KeyBinding 需要；不必为此引入框架）。</summary>
    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
