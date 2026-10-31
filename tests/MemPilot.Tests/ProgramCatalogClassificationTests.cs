using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>程序清单作为分类证据：未知程序至少不再"未识别"，发布者证据可补类别（如 Docker Inc）。</summary>
public class ProgramCatalogClassificationTests : IDisposable
{
    private readonly string _dir;
    private readonly FeedbackStore _feedback;

    public ProgramCatalogClassificationTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-catcls-" + Guid.NewGuid().ToString("N"));
        _feedback = new FeedbackStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ProcessInfo Make(string name, string path)
        => new() { Pid = 1000, Name = name, ExecutablePath = path };

    [Fact]
    public void CatalogHit_UnknownProgram_BecomesKnownThirdParty()
    {
        var catalog = new ProgramCatalogService();
        catalog.Index(new InstalledProgram { DisplayName = "Some Tool", ExecutablePath = @"D:\tools\sometool\sometool.exe", InstallLocation = @"D:\tools\sometool" });
        var engine = new ClassificationEngine(_feedback, catalog);

        var p = Make("sometool", @"D:\tools\sometool\sometool.exe");
        engine.Classify(p, new(), new HashSet<string>());

        Assert.Equal(ProcessCategory.ThirdPartyApp, p.Category);
        Assert.StartsWith("program_catalog:", p.CategoryReason);
        Assert.Equal("Some Tool", p.ProgramDisplayName);
    }

    [Fact]
    public void CatalogPublisher_DockerInc_MapsToContainerVm()
    {
        var catalog = new ProgramCatalogService();
        // 未签名辅助组件：路径不含 \docker\、名称也不在规则里，只有清单发布者证据
        catalog.Index(new InstalledProgram
        {
            DisplayName = "Docker Helper",
            Publisher = "Docker Inc",
            ExecutablePath = @"E:\vendor\helper\helper.exe",
            InstallLocation = @"E:\vendor\helper"
        });
        var engine = new ClassificationEngine(_feedback, catalog);

        var p = Make("helper", @"E:\vendor\helper\helper.exe");
        engine.Classify(p, new(), new HashSet<string>());

        Assert.Equal(ProcessCategory.ContainerVm, p.Category);
        Assert.StartsWith("program_catalog:", p.CategoryReason);
    }

    [Fact]
    public void WithoutCatalog_UnmatchedStaysUnknown()
    {
        var engine = new ClassificationEngine(_feedback);
        var p = Make("mystery", @"D:\somewhere\mystery.exe");
        engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Unknown, p.Category);
    }
}
