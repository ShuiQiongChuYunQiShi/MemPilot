using MemPilot.Helpers;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// 联网查询加固（v0.3.8，棱镜 security/network）：
/// ① API 密钥必须加密落盘（DPAPI），config.json 里不许出现明文；
/// ② 端点必须是 https 或 localhost，否则密钥会明文过网——这条在发请求之前就要拒掉。
/// </summary>
public class NetworkHardeningTests : IDisposable
{
    private readonly string _dir;

    public NetworkHardeningTests()
    {
        _dir = Path.Combine(TestTmp.Dir, "mt-tests-net-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Dpapi_RoundTrips_AndMarksValue()
    {
        const string secret = "sk-test-1234567890";
        var protectedValue = Dpapi.Protect(secret);

        Assert.True(Dpapi.IsProtected(protectedValue));
        Assert.DoesNotContain(secret, protectedValue);
        Assert.Equal(secret, Dpapi.Unprotect(protectedValue));
    }

    [Fact]
    public void Dpapi_LegacyPlaintext_IsReturnedAsIs()
    {
        Assert.Equal("plain-key", Dpapi.Unprotect("plain-key"));
        Assert.False(Dpapi.IsProtected("plain-key"));
        Assert.Equal("", Dpapi.Unprotect(""));
    }

    [Fact]
    public void ConfigStore_SavesKeyEncrypted_AndLoadsPlaintext()
    {
        var store = new ConfigStore(_dir);
        store.Config.OnlineLookupApiKey = "sk-secret-abc";
        store.Save();

        var json = File.ReadAllText(Path.Combine(_dir, "config.json"));
        Assert.DoesNotContain("sk-secret-abc", json);   // 明文绝不落盘

        var reloaded = new ConfigStore(_dir);           // 读回来必须是明文，UI/请求直接用
        Assert.Equal("sk-secret-abc", reloaded.Config.OnlineLookupApiKey);
    }

    [Fact]
    public async Task Lookup_RejectsPlainHttpEndpoint()
    {
        var store = new ConfigStore(_dir);
        store.Config.OnlineLookupEnabled = true;
        store.Config.OnlineLookupEndpoint = "http://example.com/lookup?name={name}";
        store.Config.OnlineLookupApiKey = "sk-secret-abc";

        var svc = new OnlineLookupService { Config = store.Config };
        var p = new ProcessInfo { Pid = 1, Name = "x.exe", ExecutablePath = Path.Combine(_dir, "missing.exe") };
        var res = await svc.LookupAsync(p);

        Assert.Equal("error", res.Status);
        Assert.Contains("https", res.Summary);
    }
}
