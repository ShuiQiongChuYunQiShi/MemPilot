using MemPilot.Helpers;
using MemPilot.Services;

namespace MemPilot.App;

/// <summary>决策日志导出（v0.22.0）。</summary>
public partial class MainWindow
{
    /// <summary>导出当前视图（已按 Kind / 噪声开关过滤）到 CSV 或 JSON。</summary>
    private void ExportDecisions_Click(object sender, RoutedEventArgs e)
    {
        var entries = _vm.DecisionEntries.ToList();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = UiStrings.Get("Btn_ExportDecisions"),
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = $"mempilot-decisions-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            bool json = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var text = json ? DecisionExport.ToJson(entries) : DecisionExport.ToCsv(entries);
            // BOM：Excel 打开 CSV 时中文不乱码
            System.IO.File.WriteAllText(dlg.FileName, text,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _vm.SetActionStatus(UiStrings.F("Decisions_Exported", entries.Count, dlg.FileName));
        }
        catch (Exception ex)
        {
            Diag.Record("decisions:export", ex);
            _vm.Status = UiStrings.Get("Decisions_ExportFailed") + ex.Message;
        }
    }
}
