using MemoryTool.Models;
using MemoryTool.Services;
using Xunit;

namespace MemoryTool.Tests;

public class DecisionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestTmp.Dir, "mt-tests-dec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Log_RoundTripsThroughFile_AndIsCapped()
    {
        var log = new DecisionLog(_dir);
        for (int i = 0; i < 30; i++)
            log.Log(new DecisionEntry { Kind = "AutoCleanup", Target = $"p{i}", Decision = "跳过", Reason = "test" });

        var reloaded = new DecisionLog(_dir);
        var recent = reloaded.Recent(100);
        Assert.Equal(30, recent.Count);
        Assert.Contains(recent, e => e.Target == "p29"); // most recent survives
    }

    [Fact]
    public void Recent_ReturnsNewestFirst()
    {
        var log = new DecisionLog(_dir);
        log.Log(new DecisionEntry { Kind = "A", Target = "old" });
        log.Log(new DecisionEntry { Kind = "B", Target = "new" });
        var recent = log.Recent(2);
        Assert.Equal("new", recent[0].Target);
        Assert.Equal("old", recent[1].Target);
    }
}

public class DisposalRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestTmp.Dir, "mt-tests-disp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Add_RoundTrips_AndCapsAt50()
    {
        var reg = new DisposalRegistry(_dir);
        for (int i = 0; i < 60; i++)
            reg.Add(new DisposalRecord { Pid = i, Name = $"p{i}", ExePath = @"D:\x.exe", Action = "L4ForceKill", Exited = true });

        Assert.Equal(50, reg.All().Count);
        Assert.Equal("p59", reg.All()[0].Name); // newest first

        var reloaded = new DisposalRegistry(_dir);
        Assert.Equal(50, reloaded.All().Count);
        Assert.Contains(reloaded.All(), r => r.Name == "p59");
        Assert.DoesNotContain(reloaded.All(), r => r.Name == "p5"); // oldest dropped
    }
}

public class GameLibraryServiceTests
{
    [Fact]
    public void ParseVdfRoots_ExtractsAllLibraries()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"        "C:\\Program Files (x86)\\Steam"
                    "label"        ""
                }
                "1"
                {
                    "path"        "D:\\SteamLibrary"
                }
                "2"
                {
                    "path"        "E:\\Games\\Steam"
                }
            }
            """;
        var roots = GameLibraryService.ParseVdfRoots(vdf);
        Assert.Equal(3, roots.Count);
        Assert.Contains("C:\\Program Files (x86)\\Steam\\", roots);
        Assert.Contains("D:\\SteamLibrary\\", roots);
        Assert.Contains("E:\\Games\\Steam\\", roots);
    }

    [Fact]
    public void ParseVdfRoots_DedupesCaseInsensitively()
    {
        const string vdf = """
            "0" { "path" "D:\\SteamLibrary" }
            "1" { "path" "d:\\steamlibrary" }
            """;
        Assert.Single(GameLibraryService.ParseVdfRoots(vdf));
    }
}
