using System.Net;
using System.Net.Sockets;
using System.Text;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>单活跃租约状态机：实测达成量、409 冲突、TTL 过期、守护告警一次。</summary>
public class LeaseManagerTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);
    private static LeaseReliefResult Relief(double afterGb)
        => new(2, afterGb, "test");

    private static LeaseRequest Req(double gb = 8, int ttl = 3600)
        => new() { TargetAvailableGb = gb, TtlSeconds = ttl, Requester = "ai-script" };

    [Fact]
    public void Acquire_ReturnsMeasuredLease()
    {
        var m = new LeaseManager();
        var lease = m.Acquire(Req(8), Relief(7.4), Now);
        Assert.NotNull(lease);
        Assert.Equal(7.4, lease!.AchievedAvailableGb);
        Assert.Equal(8, lease.TargetAvailableGb);
        Assert.Equal(0.6, lease.ShortfallGb, precision: 6);
        Assert.Equal("active", lease.State);
        Assert.Equal(TimeSpan.FromHours(1), lease.ExpiresUtc - lease.StartUtc);
    }

    [Fact]
    public void SecondAcquire_WhileActive_IsRejected()
    {
        var m = new LeaseManager();
        Assert.NotNull(m.Acquire(Req(), Relief(7), Now));
        Assert.Null(m.Acquire(Req(12), Relief(11), Now.AddMinutes(5))); // 409 的数据层
    }

    [Fact]
    public void Release_ByMatchingId_Only()
    {
        var m = new LeaseManager();
        var lease = m.Acquire(Req(), Relief(7), Now)!;
        Assert.Null(m.Release("wrong-id", Now.AddMinutes(1)));
        var released = m.Release(lease.Id, Now.AddMinutes(1))!;
        Assert.Equal("released", released.State);
        Assert.Equal(Now.AddMinutes(1), released.EndUtc);
        Assert.Null(m.Current(Now.AddMinutes(2)));
    }

    [Fact]
    public void TtlExpiry_AutoExpires_AndFiresEventOnce()
    {
        var m = new LeaseManager();
        var events = new List<string>();
        m.LeaseEvent += (l, ev) => events.Add(ev);
        m.Acquire(Req(ttl: 60), Relief(7), Now);

        Assert.NotNull(m.Current(Now.AddSeconds(30)));   // 未到期
        Assert.Null(m.Current(Now.AddMinutes(2)));       // 到期 → expired
        Assert.Null(m.Current(Now.AddMinutes(3)));       // 不会重复发事件
        Assert.Equal(new[] { "开始", "过期" }, events);
    }

    [Fact]
    public void Guard_AlertsOnce_WhenEroded()
    {
        var m = new LeaseManager();
        m.Acquire(Req(), Relief(7), Now);                // 授予下限 = 7 - 1 = 6GB

        var mem = new SystemMemoryInfo { AvailablePhysicalBytes = (long)(6.5 * 1024 * 1024 * 1024) };
        Assert.Null(m.CheckGuard(mem, Now.AddMinutes(1))); // 还没跌破

        var eroded = new SystemMemoryInfo { AvailablePhysicalBytes = (long)(4.0 * 1024 * 1024 * 1024) };
        var hit = m.CheckGuard(eroded, Now.AddMinutes(2)); // 跌破 → 告警一次
        Assert.NotNull(hit);
        Assert.Contains("租约被侵蚀", hit!.Details);

        var hit2 = m.CheckGuard(eroded, Now.AddMinutes(3)); // 不会重复刷
        Assert.Null(hit2);
    }

    [Fact]
    public void Ttl_IsClamped()
    {
        var m = new LeaseManager();
        var lease = m.Acquire(Req(ttl: 999999), Relief(7), Now)!;
        Assert.Equal(TimeSpan.FromHours(24), lease.ExpiresUtc - lease.StartUtc);
    }
}

/// <summary>
/// 租约 API（真实 TCP 回环）：鉴权 401、流程 200、单活跃 409、坏 JSON 400、未知 404。
/// 执行器是注入的假实现（不做真实 trim），只验证协议与状态机。
/// </summary>
public class LeaseApiTests : IDisposable
{
    private readonly LeaseManager _manager = new();
    private readonly LeaseApiService _api;
    private readonly int _port;
    private const string Token = "test-token-0123456789abcdef";
    private int _reliefCalls;

    public LeaseApiTests()
    {
        _api = new LeaseApiService(
            mem: () => new SystemMemoryInfo
            {
                AvailablePhysicalBytes = (long)(6.4 * 1024 * 1024 * 1024),
                TotalPhysicalBytes = (long)(31.7 * 1024 * 1024 * 1024),
                TotalCommitLimitBytes = (long)(55.4 * 1024 * 1024 * 1024),
                TotalCommittedBytes = (long)(38.0 * 1024 * 1024 * 1024)
            },
            reliefExecutor: () => { _reliefCalls++; return new LeaseReliefResult(2, 8.2, "fake relief"); },
            manager: _manager,
            token: () => Token,
            port: () => 0);   // 随机端口
        _api.Start();
        _port = _api.BoundPort;
    }

    public void Dispose() => _api.Dispose();

    private async Task<(int status, string body)> Call(string method, string path, string? body = null, string? token = Token)
    {
        using var client = new TcpClient("127.0.0.1", _port);
        var stream = client.GetStream();
        var sb = new StringBuilder();
        sb.Append($"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n");
        if (token != null) sb.Append($"Authorization: Bearer {token}\r\n");
        if (body != null) sb.Append($"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        sb.Append("\r\n");
        if (body != null) sb.Append(body);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes);
        var buf = new byte[64 * 1024];
        var ms = new MemoryStream();
        int n;
        while ((n = await stream.ReadAsync(buf)) > 0) ms.Write(buf, 0, n);
        var raw = Encoding.UTF8.GetString(ms.ToArray());
        var line1 = raw.Split("\r\n")[0];
        int status = int.Parse(line1.Split(' ')[1]);
        var bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (status, bodyStart < 0 ? "" : raw[(bodyStart + 4)..]);
    }

    [Fact]
    public async Task NoToken_Is401()
    {
        var (status, _) = await Call("GET", "/api/status", token: null);
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task WrongToken_Is401()
    {
        var (status, _) = await Call("GET", "/api/status", token: "nope");
        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Status_ReturnsMemoryAndVersion()
    {
        var (status, body) = await Call("GET", "/api/status");
        Assert.Equal(200, status);
        Assert.Contains("availableGb", body);
        Assert.Contains("commitFreePct", body);
    }

    [Fact]
    public async Task UnknownPath_Is404()
    {
        var (status, _) = await Call("GET", "/api/nope");
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task LeaseFlow_Acquire_Conflict_Get_Release()
    {
        var (s1, b1) = await Call("POST", "/api/lease", """{"targetAvailableGb":6,"ttlSeconds":600,"requester":"ai"}""");
        Assert.Equal(200, s1);
        Assert.Contains("\"achievedAvailableGb\":8.2", b1);   // 实测达成量（假执行器给 8.2）
        Assert.Contains("\"state\":\"active\"", b1);

        var (s2, _) = await Call("POST", "/api/lease", """{"targetAvailableGb":6}""");
        Assert.Equal(409, s2);                                // 单活跃：冲突

        var (s3, b3) = await Call("GET", "/api/lease");
        Assert.Equal(200, s3);
        Assert.Contains("ai", b3);

        var (s4, _) = await Call("DELETE", "/api/lease/nope");
        Assert.Equal(404, s4);
    }

    [Fact]
    public async Task BadJson_Is400()
    {
        var (status, body) = await Call("POST", "/api/lease", "{oops");
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Delete_ActiveLease_Releases()
    {
        var (_, b1) = await Call("POST", "/api/lease", """{"targetAvailableGb":6}""");
        var id = System.Text.Json.JsonDocument.Parse(b1).RootElement.GetProperty("id").GetString()!;
        var (status, body) = await Call("DELETE", $"/api/lease/{id}");
        Assert.Equal(200, status);
        Assert.Contains("released", body);
        var (s2, b2) = await Call("POST", "/api/lease", """{"targetAvailableGb":6}""");
        Assert.Equal(200, s2);   // 归还后可再次申请
    }

    [Fact]
    public async Task ReliefExecutor_Ran_OncePerAcquire()
    {
        await Call("POST", "/api/lease", """{"targetAvailableGb":6}""");
        Assert.Equal(1, _reliefCalls);
    }
}
