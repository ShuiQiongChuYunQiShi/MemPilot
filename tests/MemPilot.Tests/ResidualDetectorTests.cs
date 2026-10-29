using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

public class ResidualDetectorTests
{
    private static HashSet<string> Running(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void QQLiveService_WithoutHost_IsOrphan()
    {
        var host = ResidualDetector.FindOrphanHost("QQLiveService", Running("explorer", "Weixin"));
        Assert.Equal("qqlive", host);
    }

    [Fact]
    public void QQLiveService_WithHost_IsNotOrphan()
    {
        Assert.Null(ResidualDetector.FindOrphanHost("QQLiveService", Running("QQLive", "explorer")));
    }

    [Fact]
    public void WeTypeUpdate_AnyImePartAlive_IsNotOrphan()
    {
        Assert.Null(ResidualDetector.FindOrphanHost("wetype_update", Running("wetype_renderer")));
        Assert.NotNull(ResidualDetector.FindOrphanHost("wetype_update", Running("explorer")));
    }

    [Fact]
    public void UnknownProcess_IsNeverOrphan()
    {
        Assert.Null(ResidualDetector.FindOrphanHost("someapp", Running()));
    }
}
