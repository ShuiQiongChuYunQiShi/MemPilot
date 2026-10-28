using MemPilot.Models;
using MemPilot.Services;
using Xunit;
using Xunit.Abstractions;

namespace MemPilot.Tests;

/// <summary>Security invariants of the rule-based classifier.</summary>
public class ClassificationEngineTests : IDisposable
{
    private readonly string _dir;
    private readonly ClassificationEngine _engine;
    private readonly FeedbackStore _feedback;
    private readonly ITestOutputHelper _out;

    public ClassificationEngineTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-classify-" + Guid.NewGuid().ToString("N"));
        _feedback = new FeedbackStore(_dir);
        _engine = new ClassificationEngine(_feedback);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ProcessInfo Make(string name, string path, bool verified = false, string subject = "")
        => new()
        {
            Pid = 1000,
            Name = name,
            ExecutablePath = path,
            SignatureVerified = verified,
            SignatureSubject = subject
        };

    [Fact]
    public void SystemCritical_NamesAreAlwaysProtected_EvenUnsigned()
    {
        var p = Make("svchost", @"C:\Windows\System32\svchost.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.SystemCritical, p.Category);
        Assert.True(p.IsProtected);
    }

    [Fact]
    public void FakeMicrosoftPath_WithoutTrustedSignature_IsNotMarkedSigned()
    {
        // forged cert blob (CreateFromSignedFile-only era would trust this); WinVerifyTrust gate must hold
        var p = Make("evilsvc", @"C:\Windows\System32\evilsvc.exe", verified: false);
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.NotEqual("system_path_signed", p.CategoryReason);
        Assert.False(p.SignatureVerified);
        Assert.True(p.IsSystemProcess); // system path fallback still keeps it out of cleanup
    }

    [Fact]
    public void DefenderSoundingName_IsNotSecuritySoftware_UnlessExactRule()
    {
        var p = Make("defenderclone", @"C:\Users\me\AppData\Local\defenderclone.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.NotEqual(ProcessCategory.SecuritySoftware, p.Category);
    }

    // Regression for the old bare-"ea" substring bug: "Teams" matched GameLauncher via "ea".
    [Theory]
    [InlineData("Teams", @"C:\Program Files\Microsoft\Teams\teams.exe")]
    [InlineData("Weather", @"C:\Program Files\WindowsApps\weather.exe")]
    [InlineData("SearchHost", @"C:\Windows\SystemApps\SearchHost.exe")]
    public void CommonApps_ContainingEaOrBe_DoNotBecomeGameLaunchers(string name, string path)
    {
        var p = Make(name, path, verified: true, subject: "CN=Microsoft Corporation");
        _engine.Classify(p, new(), new HashSet<string>());
        _out.WriteLine($"{name} -> {p.Category} ({p.CategoryReason})");
        Assert.NotEqual(ProcessCategory.GameLauncher, p.Category);
    }

    [Fact]
    public void PublisherRule_ByTrustedSignature_WinsOverGamePath()
    {
        var p = Make("foo", @"C:\Games\Foo\foo.exe", verified: true, subject: "CN=Valve Corporation, O=Valve Corporation");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.GameLauncher, p.Category);
        Assert.Equal("publisher:valve", p.CategoryReason);
    }

    [Fact]
    public void UntrustedSubject_ContainingValve_DoesNotTriggerPublisherRule()
    {
        var p = Make("foo", @"C:\Games\Foo\foo.exe", verified: false, subject: "CN=Valve Corporation");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.NotEqual("publisher:valve", p.CategoryReason);
    }

    [Fact]
    public void ExactLauncherNames_AreGameLaunchers()
    {
        var p = Make("EpicGamesLauncher", @"C:\Program Files\Epic Games\Launcher.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.GameLauncher, p.Category);
    }

    [Fact]
    public void AntiCheat_IsProtected()
    {
        var p = Make("BEService", @"C:\Program Files (x86)\BattlEye\BEService.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.AntiCheat, p.Category);
        Assert.True(p.IsProtected);
    }

    [Fact]
    public void UnknownPath_StaysUnknown()
    {
        var p = Make("weirdthing", @"D:\somewhere\weirdthing.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Unknown, p.Category);
        Assert.Equal("unclassified", p.CategoryReason);
    }

    [Fact]
    public void DriverPath_IsProtected()
    {
        var p = Make("nvlddmkm", @"C:\Windows\System32\DriverStore\nvlddmkm.sys");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Driver, p.Category);
        Assert.True(p.IsProtected);
    }

    [Fact]
    public void FeedbackOverride_Wins()
    {
        _feedback.Add(new FeedbackEntry { ProcessName = "mytool", ExecutablePath = @"D:\tools\mytool.exe", ReportedCategory = ProcessCategory.Game });
        var p = Make("mytool", @"D:\tools\mytool.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Game, p.Category);
        Assert.Equal("user_feedback", p.CategoryReason);
    }
}
