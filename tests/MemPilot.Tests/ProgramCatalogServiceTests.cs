using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>程序清单（v0.3.0）：注册表解析、路径索引、持久化。</summary>
public class ProgramCatalogServiceTests : IDisposable
{
    private readonly string _dir;

    public ProgramCatalogServiceTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ProgramFromValues_FiltersSystemComponents_AndStripsIconIndex()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Example App",
            ["Publisher"] = "Example Inc",
            ["DisplayVersion"] = "1.2.3",
            ["DisplayIcon"] = @"%ProgramFiles%\Example\example.exe,0",
            ["InstallLocation"] = @"""C:\Program Files\Example""",
        };
        var p = ProgramCatalogService.ProgramFromValues(values, "registry");
        Assert.NotNull(p);
        Assert.Equal("Example App", p!.DisplayName);
        Assert.Equal("Example Inc", p.Publisher);
        Assert.Equal("1.2.3", p.Version);
        Assert.EndsWith(@"\Example\example.exe", p.ExecutablePath.Replace("/", @"\"));
        Assert.DoesNotContain(",0", p.ExecutablePath);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Example"), p.InstallLocation);

        var sys = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        { ["DisplayName"] = "KB123", ["SystemComponent"] = 1 };
        Assert.Null(ProgramCatalogService.ProgramFromValues(sys, "registry"));

        var noName = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Publisher"] = "X" };
        Assert.Null(ProgramCatalogService.ProgramFromValues(noName, "registry"));
    }

    [Fact]
    public void ProgramFromValues_ExtractsQuotedUninstallPath()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Tool",
            ["UninstallString"] = @"""C:\Program Files\Tool\uninst.exe"" /S",
        };
        var p = ProgramCatalogService.ProgramFromValues(values, "registry");
        Assert.NotNull(p);
        Assert.Equal(@"C:\Program Files\Tool\uninst.exe", p!.ExecutablePath);
    }

    [Fact]
    public void FindByPath_ExactThenLongestPrefix_WithBoundaryCheck()
    {
        var catalog = new ProgramCatalogService();
        catalog.Index(new InstalledProgram { DisplayName = "Docker", InstallLocation = @"C:\Program Files\Docker", ExecutablePath = @"C:\Program Files\Docker\Docker\Docker Desktop.exe" });
        catalog.Index(new InstalledProgram { DisplayName = "Docker Engine", InstallLocation = @"C:\Program Files\Docker\Docker", ExecutablePath = @"C:\Program Files\Docker\Docker\dockerd.exe" });
        catalog.Index(new InstalledProgram { DisplayName = "Abc", InstallLocation = @"C:\Program Files\Abc", ExecutablePath = @"C:\Program Files\Abc\abc.exe" });

        Assert.Equal("Docker Engine", catalog.FindByPath(@"C:\Program Files\Docker\Docker\resources\com.docker.backend.exe")!.DisplayName);
        Assert.Equal("Docker", catalog.FindByPath(@"C:\Program Files\Docker\other\tool.exe")!.DisplayName);
        Assert.Equal("Docker Engine", catalog.FindByPath(@"C:\Program Files\Docker\Docker\dockerd.exe")!.DisplayName); // 精确路径优先
        Assert.Null(catalog.FindByPath(@"C:\Program Files\Abcd\evil.exe")); // 目录边界：Abc 不能匹配 Abcd
        Assert.Null(catalog.FindByPath(@"D:\unrelated\app.exe"));
    }

    [Fact]
    public void Index_DedupeFirstWins()
    {
        var catalog = new ProgramCatalogService();
        catalog.Index(new InstalledProgram { DisplayName = "FromRegistry", ExecutablePath = @"D:\apps\x\tool.exe" });
        catalog.Index(new InstalledProgram { DisplayName = "FromScan", ExecutablePath = @"D:\apps\x\tool.exe" });
        Assert.Equal(1, catalog.Count);
        Assert.Equal("FromRegistry", catalog.FindByPath(@"D:\apps\x\tool.exe")!.DisplayName);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var catalog = new ProgramCatalogService { BuiltUtc = DateTime.UtcNow };
        catalog.Index(new InstalledProgram { DisplayName = "App A", ExecutablePath = @"D:\a\a.exe", InstallLocation = @"D:\a", Publisher = "Pa" });
        catalog.Index(new InstalledProgram { DisplayName = "App B", ExecutablePath = @"D:\b\b.exe", InstallLocation = @"D:\b", Publisher = "Pb" });
        catalog.Save(_dir);

        var loaded = ProgramCatalogService.TryLoad(Path.Combine(_dir, "programs.json"));
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Equal("Pa", loaded.FindByPath(@"D:\a\a.exe")!.Publisher);
        Assert.Equal("App B", loaded.FindByPath(@"D:\b\sub\helper.exe")!.DisplayName);
    }
}
