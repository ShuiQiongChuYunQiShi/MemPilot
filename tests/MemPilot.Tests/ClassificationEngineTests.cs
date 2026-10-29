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

    // ---- 2026-10-29 规则库扩充：垃圾常驻 / 按需后台 / 精确名覆盖发布者 ----

    [Theory]
    [InlineData("wpscenter", @"C:\Users\me\AppData\Local\Kingsoft\WPS Office\12.1.0.29161\office6\wpscenter.exe")]
    [InlineData("wpscloudsvr", @"C:\Users\me\AppData\Local\Kingsoft\WPS Office\12.1.0.29161\office6\wpscloudsvr.exe")]
    [InlineData("AlibabaProtect", @"C:\Program Files (x86)\AlibabaProtect\1.0.70.3194\AlibabaProtect.exe")]
    [InlineData("wetype_update", @"C:\Program Files\Tencent\WeType\3.0.0.38\wetype_update.exe")]
    [InlineData("lghub_updater", @"C:\Program Files\LGHUB\lghub_updater.exe")]
    public void JunkBackgroundHelpers_AreBloatware_AndNotProtected(string name, string path)
    {
        var p = Make(name, path);
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Bloatware, p.Category);
        Assert.Equal("junk_name", p.CategoryReason);
        Assert.False(p.IsProtected);
    }

    [Fact]
    public void KingsoftOfficeSignature_IsBloatware()
    {
        var p = Make("wps", @"C:\Users\me\AppData\Local\Kingsoft\WPS Office\12.1.0.29161\office6\wps.exe",
            verified: true, subject: @"CN=""Zhuhai Kingsoft Office Software Co., Ltd."", O=""Zhuhai Kingsoft Office Software Co., Ltd.""");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Bloatware, p.Category);
        Assert.Equal("junk_vendor", p.CategoryReason);
    }

    // 腾讯签名同时覆盖游戏与微信输入法：tencent→游戏平台 的发布者规则不得吞掉 IME（旧代码下本测试失败）
    [Fact]
    public void WeTypeIme_BeatsTencentPublisherRule()
    {
        var p = Make("wetype_server", @"C:\Program Files\Tencent\WeType\3.0.0.38\wetype_server.exe",
            verified: true, subject: "CN=Tencent Technology (Shenzhen) Company Limited");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.ThirdPartyApp, p.Category);
        Assert.Equal("ime_name", p.CategoryReason);
        Assert.NotEqual(ProcessCategory.GameLauncher, p.Category);
    }

    // 腾讯系消费级应用同理：QQ/微信/WeChatAppEx 都是 Tencent 签名，不得被 tencent→游戏平台 吞掉
    [Theory]
    [InlineData("QQ", @"D:\Program Files (x86)\Tencent\QQNT\QQ.exe")]
    [InlineData("Weixin", @"D:\Program Files\Tencent\Weixin\Weixin.exe")]
    [InlineData("WeChatAppEx", @"C:\Users\Administrator\AppData\Roaming\Tencent\xwechat\xplugin\plugins\RadiumWMPF\WeChatAppEx.exe")]
    public void TencentConsumerApps_BeatTencentPublisherRule(string name, string path)
    {
        var p = Make(name, path, verified: true, subject: "CN=Tencent Technology (Shenzhen) Company Limited");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.ThirdPartyApp, p.Category);
        Assert.Equal("tencent_app_name", p.CategoryReason);
        Assert.NotEqual(ProcessCategory.GameLauncher, p.Category);
    }

    [Fact]
    public void ChineseGameDirectory_IsGame()
    {
        var p = Make("抽卡版PVZ", @"F:\游戏\7.抽卡版植物大战僵尸（电脑+手机版）\抽卡0.35.2\抽卡版PVZ.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Game, p.Category);
        Assert.Equal("game_path", p.CategoryReason);
    }

    [Fact]
    public void WeGameAppsDirectory_IsGame()
    {
        var p = Make("NZMClient", @"F:\WeGameApps\rail_apps\逆战：未来(2002130)\NZM\Binaries\Win64\NZMClient.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Game, p.Category);
        Assert.Equal("game_path", p.CategoryReason);
    }

    // 安全守卫：ACE 反作弊住在 WeGame 游戏目录里，游戏路径规则不得把它带进清理池——
    // 反作弊判定先于游戏路径，且必须受保护。
    [Theory]
    [InlineData("ACE-Helper", @"F:\WeGameApps\rail_apps\逆战：未来(2002130)\NZM\Binaries\Win64\AntiCheatExpert\ACE-Helper.exe")]
    [InlineData("SGuardUpdate64", @"F:\WeGameApps\rail_apps\逆战：未来(2002130)\NZM\Binaries\Win64\AntiCheatExpert\SGuard\x64\SGuardUpdate64.exe")]
    public void AntiCheatInsideGameDirectory_IsProtectedAntiCheat_NotGame(string name, string path)
    {
        var p = Make(name, path);
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.AntiCheat, p.Category);
        Assert.True(p.IsProtected);
    }

    [Fact]
    public void OllamaLlamaServer_IsAiTool()
    {
        var p = Make("llama-server", @"D:\Users\me\AppData\Local\Programs\ollama\llama-server.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.AiImageVideo, p.Category);
        Assert.Equal("ai_tool", p.CategoryReason);
    }

    // vivo 办公套件：偶发使用 → 后台服务可清理，但不进垃圾池、不受保护（结束≠卸载，随宿主程序恢复）
    [Theory]
    [InlineData("vivoesService_x64", @"C:\Program Files (x86)\pcsuite\vivoesService\x64\vivoesService_x64.exe")]
    [InlineData("vivoSyncService", @"C:\Program Files (x86)\pcsuite\SyncService\vivoSyncService.exe")]
    public void VivoPcSuiteServices_AreOnDemandBackground_NotBloatware(string name, string path)
    {
        var p = Make(name, path, verified: true, subject: @"CN=""vivo Mobile Communication Co., Ltd""");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.BackgroundService, p.Category);
        Assert.Equal("on_demand_service", p.CategoryReason);
        Assert.False(p.IsProtected);
    }

    [Fact]
    public void AsusHelpers_AreOnDemandBackground_ViaSignature()
    {
        var p = Make("SomeAsusHelper", @"C:\Program Files (x86)\ASUS\ArmouryDevice\somehelper.exe",
            verified: true, subject: "CN=ASUSTeK COMPUTER INC., O=ASUSTeK COMPUTER INC.");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.BackgroundService, p.Category);
    }

    [Fact]
    public void OrdinaryUnmatchedApp_StaysUnknown_NotBloatware()
    {
        var p = Make("myrandomapp", @"D:\somewhere\myrandomapp.exe");
        _engine.Classify(p, new(), new HashSet<string>());
        Assert.Equal(ProcessCategory.Unknown, p.Category);
    }
}
