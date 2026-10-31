using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MemPilot.Models;
using MemPilot.Services;

namespace MemPilot.ViewModels;

/// <summary>设置页：程序清单、影子校准与落盘诊断文本。</summary>
public partial class MainViewModel
{
    // ---- 程序清单（v0.3.0）----

    public ProgramCatalogService ProgramCatalog { get; private set; }

    private string _programCatalogInfo = "";
    public string ProgramCatalogInfo
    {
        get => _programCatalogInfo;
        private set { _programCatalogInfo = value; OnPropertyChanged(); }
    }

    // ---- 场景影子校准（v0.3.2）：影子只写不读 → 这里给出结算，设置页可见 ----

    private string _shadowCalibrationText = "尚未统计（打开设置页或点「刷新校准数据」）。";
    public string ShadowCalibrationText
    {
        get => _shadowCalibrationText;
        private set { _shadowCalibrationText = value; OnPropertyChanged(); }
    }

    /// <summary>重算近 7 天影子结算文本。失败要说出来，不能静默留旧值。</summary>
    public void RefreshShadowCalibration(int days = 7)
    {
        try
        {
            var report = SceneShadowReport.Build(Decisions.Recent(1000), Audit.Recent(500), DateTime.UtcNow, days);
            ShadowCalibrationText = report.Text;
        }
        catch (Exception ex)
        {
            ShadowCalibrationText = "影子校准统计失败：" + ex.Message;
        }
    }

    // ---- 持久化诊断（v0.3.2）：JsonFileStore 的失败计数，设置页可见 ----

    private string _storeDiagnosticsText = "";
    public string StoreDiagnosticsText
    {
        get => _storeDiagnosticsText;
        private set { _storeDiagnosticsText = value; OnPropertyChanged(); }
    }

    public void RefreshDiagnostics()
    {
        var summary = MemPilot.Helpers.StoreDiagnostics.Summary();
        var recent = MemPilot.Helpers.StoreDiagnostics.Recent().FirstOrDefault();
        StoreDiagnosticsText = recent == null ? summary : summary + "\n最近：" + recent;
    }

    private void BuildProgramCatalogInBackground()
    {
        try
        {
            var built = ProgramCatalogService.Build(msg => SetUi(() => Status = msg));
            built.Save(ConfigStore.DataDirectory);
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ProgramCatalog = built;
                Classification.Catalog = built;
                ProgramCatalogInfo = $"程序清单：{built.Count} 条（{built.BuiltUtc.ToLocalTime():MM-dd HH:mm}）";
            });
        }
        catch { }
    }

    /// <summary>手动重建程序清单（设置页按钮）：注册表 + 开始菜单 + 安装根。</summary>
    public async Task<string> RebuildProgramCatalogAsync()
    {
        Status = "正在重建程序清单（注册表 + 开始菜单 + 安装根）…";
        var built = await Task.Run(() => ProgramCatalogService.Build(msg => SetUi(() => Status = msg))).ConfigureAwait(true);
        built.Save(ConfigStore.DataDirectory);
        ProgramCatalog = built;
        Classification.Catalog = built;
        ProgramCatalogInfo = $"程序清单：{built.Count} 条（{built.BuiltUtc.ToLocalTime():MM-dd HH:mm}）";
        Status = $"程序清单已重建：{built.Count} 条（目录索引 {built.DirectoryIndexCount}）";
        return Status;
    }
}
