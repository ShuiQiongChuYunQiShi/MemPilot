namespace MemPilot.App;

/// <summary>提交告急 L3 非模态确认卡（v0.11.0 P1-3）的按钮处理。</summary>
public partial class MainWindow
{
    private void ConfirmCommitRelief_Click(object sender, RoutedEventArgs e) => _vm.ConfirmCommitRelief();
    private void DismissCommitRelief_Click(object sender, RoutedEventArgs e) => _vm.DismissCommitRelief();
}
