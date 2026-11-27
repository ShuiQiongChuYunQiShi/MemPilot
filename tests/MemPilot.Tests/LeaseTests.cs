using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    // ---- v0.23.0 续期（renew）：长任务跑过 TTL 不该被自己申请的内存反噬 ----

    [Fact]
    public void Renew_ExtendsExpiry_AndCountsRenewals()
    {
        var m = new LeaseManager();
        var lease = m.Acquire(Req(ttl: 60), Relief(7), Now)!;   // 到期 = Now+60s

        var (renewed, expired) = m.Renew(lease.Id, 300, Now.AddSeconds(45));
        Assert.False(expired);
        Assert.NotNull(renewed);
        Assert.Equal(Now.AddSeconds(345), renewed!.ExpiresUtc);  // 从续期时刻重新计 300s
        Assert.Equal(1, renewed.RenewCount);

        var (again, _) = m.Renew(lease.Id, 60, Now.AddSeconds(340));
        Assert.NotNull(again);
        Assert.Equal(2, again!.RenewCount);
        Assert.Equal(Now.AddSeconds(400), again.ExpiresUtc);
    }

    [Fact]
    public void Renew_WrongIdOrAlreadyReleased_ReturnsNullNotExpired()
    {
        var m = new LeaseManager();
        var lease = m.Acquire(Req(), Relief(7), Now)!;

        Assert.Null(m.Renew("wrong-id", 60, Now.AddSeconds(10)).lease);   // 404
        Assert.False(m.Renew("wrong-id", 60, Now.AddSeconds(10)).expired);

        m.Release(lease.Id, Now.AddSeconds(20));
        var (afterRelease, expiredAfterRelease) = m.Renew(lease.Id, 60, Now.AddSeconds(30));
        Assert.Null(afterRelease);
        Assert.False(expiredAfterRelease);
    }

    [Fact]
    public void Renew_AfterExpiry_ReportsExpired_AndRestoresSuspended()
    {
        var m = new LeaseManager();
        var events = new List<string>();
        bool restored = false;
        m.RestoreHook = l => { restored = true; return (1, 0); };
        m.LeaseEvent += (l, ev) => events.Add(ev);

        var lease = m.Acquire(Req(ttl: 60), Relief(7), Now)!;
        var (renewed, expired) = m.Renew(lease.Id, 600, Now.AddMinutes(2));

        Assert.Null(renewed);
        Assert.True(expired);                    // API 据此返回 410，而不是 404
        Assert.True(restored);                   // 过期必须恢复挂起进程（不能只记账）
        Assert.Contains("过期", events);
        Assert.Equal("expired", lease.State);
    }

    [Fact]
    public void Renew_DoesNotSwallowAGuardAlert()
    {
        // 续期只延长时间，不该重置"已被侵蚀"的告警状态——
        // 否则调用方可以用 renew 把真实的侵蚀告警刷掉
        var m = new LeaseManager();
        var lease = m.Acquire(Req(), Relief(7), Now)!;
        var eroded = new SystemMemoryInfo { AvailablePhysicalBytes = (long)(4.0 * 1024 * 1024 * 1024) };
        Assert.NotNull(m.CheckGuard(eroded, Now.AddMinutes(1)));

        m.Renew(lease.Id, 600, Now.AddMinutes(2));

        Assert.Null(m.CheckGuard(eroded, Now.AddMinutes(3)));   // 仍然只告警一次
    }

    [Fact]
    public void RestoreHook_RunsBeforeEvent_SoResponseSeesResults()    {
        var m = new LeaseManager();
        var order = new List<string>();
        var suspended = new List<LeaseSuspendedProcess>
        { new() { Pid = 7, Name = "idle.exe", StartTime = Now, PrivateBytes = 1L << 30 } };

        // 钩子模拟"恢复进程"：改写账本里的 Restored
        m.RestoreHook = lease =>
        {
            order.Add("restore");
            int ok = 0;
            foreach (var s in lease.Suspended) { s.Restored = true; s.RestoreNote = "进程已恢复"; ok++; }
            return (ok, 0);
        };
        m.LeaseEvent += (l, ev) => { if (ev == "归还") order.Add("event:" + l.Suspended.Count(s => s.Restored)); };

        var lease = m.Acquire(Req(), Relief(7), Now, LeasePolicy.Hard, suspended)!;
        var released = m.Release(lease.Id, Now.AddMinutes(1))!;

        Assert.Equal(new[] { "restore", "event:1" }, order);   // 先恢复，事件才看得到结果
        Assert.True(released.Suspended[0].Restored);            // 返回给调用方的账本已更新
        Assert.Equal("进程已恢复", released.Suspended[0].RestoreNote);
    }

    [Fact]
    public void Expiry_AlsoRunsRestoreHook()
    {
        var m = new LeaseManager();
        int calls = 0;
        m.RestoreHook = lease => { calls++; return (0, 0); };
        var suspended = new List<LeaseSuspendedProcess> { new() { Pid = 9, Name = "x.exe", StartTime = Now } };
        m.Acquire(Req(ttl: 60), Relief(7), Now, LeasePolicy.Hard, suspended);

        Assert.Null(m.Current(Now.AddMinutes(2)));
        Assert.Equal(1, calls);
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
    private LeasePolicy _lastPolicy = LeasePolicy.Soft;
    private int _lastRequesterPid = -1;

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
            reliefExecutor: (req, policy) =>
            {
                _reliefCalls++;
                _lastPolicy = policy;
                _lastRequesterPid = req.RequesterPid;
                var suspended = policy == LeasePolicy.Hard
                    ? new List<LeaseSuspendedProcess> { new() { Pid = 4242, Name = "idle.exe", StartTime = DateTime.UtcNow } }
                    : new List<LeaseSuspendedProcess>();
                return (new LeaseReliefResult(2, 8.2, "fake relief"), suspended);
            },
            manager: _manager,
            token: () => Token,
            port: () => 0);   // 随机端口
        Assert.True(_api.Start(), "随机端口应能监听");
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
    public async Task HardPolicy_SuspendsAndReportsList()
    {
        var (status, body) = await Call("POST", "/api/lease",
            """{"targetAvailableGb":8,"policy":"hard","requester":"ai"}""");
        Assert.Equal(200, status);
        Assert.Equal(LeasePolicy.Hard, _lastPolicy);
        Assert.Contains("\"policy\":\"hard\"", body);
        Assert.Contains("\"suspended\":", body);

        var id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        var (s2, b2) = await Call("DELETE", $"/api/lease/{id}");
        Assert.Equal(200, s2);
        Assert.Contains("4242", b2);                 // 挂起列表随归还返回，调用方可见
        var (s3, _) = await Call("GET", "/api/lease");
        Assert.Equal(404, s3);                       // 归还后无活跃租约
    }

    [Fact]
    public async Task DefaultPolicy_IsSoft()
    {
        var (_, body) = await Call("POST", "/api/lease", """{"targetAvailableGb":6}""");
        Assert.Equal(LeasePolicy.Soft, _lastPolicy);
        Assert.Contains("\"policy\":\"soft\"", body);
        Assert.Contains("\"suspended\":[]", body);
    }

    [Fact]
    public async Task ReliefExecutor_Ran_OncePerAcquire()
    {
        await Call("POST", "/api/lease", """{"targetAvailableGb":6}""");
        Assert.Equal(1, _reliefCalls);
    }

    [Fact]
    public async Task Renew_ExtendsLease_AndReportsRemaining()
    {
        var (s1, b1) = await Call("POST", "/api/lease",
            """{"targetAvailableGb":6,"ttlSeconds":60,"requester":"ai"}""");
        Assert.Equal(200, s1);
        var id = ExtractId(b1);

        var (status, body) = await Call("POST", $"/api/lease/{id}/renew", """{"ttlSeconds":600}""");
        Assert.Equal(200, status);
        Assert.Contains("\"renewCount\":1", body);
        Assert.Contains("remainingSeconds", body);   // agent 能直接看"还能用多久"
        Assert.Equal(1, _reliefCalls);               // 续期不重新腾挪（只有 acquire 那一次）
    }

    [Fact]
    public async Task Renew_WrongId_Is404()
    {
        var (status, _) = await Call("POST", "/api/lease/does-not-exist/renew", """{"ttlSeconds":60}""");
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Renew_ThenRelease_StillWorks()
    {
        var (_, b1) = await Call("POST", "/api/lease", """{"targetAvailableGb":6,"requester":"ai"}""");
        var id = ExtractId(b1);
        await Call("POST", $"/api/lease/{id}/renew", """{"ttlSeconds":600}""");

        var (status, body) = await Call("DELETE", $"/api/lease/{id}");
        Assert.Equal(200, status);
        Assert.Contains("\"state\":\"released\"", body);
    }

    private static string ExtractId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    // ---- v0.23.0: requesterPid is carried through and auditable ----
    [Fact]
    public async Task RequesterPid_IsPassedToRelief_AndEchoedInResponse()
    {
        var (status, body) = await Call("POST", "/api/lease",
            """{"targetAvailableGb":6,"requester":"claude-code","requesterPid":4321}""");
        Assert.Equal(200, status);
        Assert.Equal(4321, _lastRequesterPid);          // relief 执行前就能看到它，才能排除自身子树
        Assert.Contains("\"requesterPid\":4321", body); // 账本里可核对
    }

    [Fact]
    public async Task Conflict_ReportsHolderAndRetryAfter()
    {
        await Call("POST", "/api/lease", """{"targetAvailableGb":6,"requester":"first","ttlSeconds":600}""");
        var (status, body) = await Call("POST", "/api/lease", """{"targetAvailableGb":6,"requester":"second"}""");

        Assert.Equal(409, status);
        Assert.Contains("\"error\":\"lease_active\"", body);
        Assert.Contains("\"requester\":\"first\"", body);     // 等待方知道是谁占着
        Assert.Contains("\"holder\"", body);
        Assert.Contains("\"retryAfterSeconds\"", body);      // 知道该等多久
        Assert.Contains("\"remainingSeconds\"", body);
    }

    [Fact]
    public async Task ManualRequest_PreemptsAutomaticReservation()
    {
        // v0.25.0：自动预留（requester 以 auto: 开头）给手动申请让位——
        // "帮你预留"不该变成"把你挡在门外"。
        await Call("POST", "/api/lease",
            """{"targetAvailableGb":6,"requester":"auto:ai-session","ttlSeconds":43200}""");
        var (status, body) = await Call("POST", "/api/lease",
            """{"targetAvailableGb":6,"requester":"manual"}""");

        Assert.Equal(200, status);
        Assert.Contains("\"requester\":\"manual\"", body);
    }

    [Fact]
    public async Task ManualRequest_DoesNotPreemptAnotherManualLease()
    {
        await Call("POST", "/api/lease", """{"targetAvailableGb":6,"requester":"first"}""");
        var (status, body) = await Call("POST", "/api/lease", """{"targetAvailableGb":6,"requester":"second"}""");

        Assert.Equal(409, status);                       // 手动之间仍然互斥
        Assert.Contains("\"requester\":\"first\"", body);
    }

    [Fact]
    public async Task NegativeRequesterPid_Is400_AndNoReliefRuns()
    {
        var (status, body) = await Call("POST", "/api/lease",
            """{"targetAvailableGb":6,"requester":"x","requesterPid":-1}""");
        Assert.Equal(400, status);
        Assert.Contains("requesterPid", body);
        Assert.Equal(0, _reliefCalls);                  // 校验发生在占位/腾挪之前，不留脏账
    }

    [Fact]
    public async Task Plan_WithoutPlanner_Is503_NotAFakeSuccess()
    {
        // App 没注入 planner 时（例如未来的 CLI 宿主），必须老实说"不支持"，
        // 而不是返回一个全 0 的 plan 让调用方以为"可行"
        var (status, body) = await Call("POST", "/api/plan", """{"targetAvailableGb":4}""");
        Assert.Equal(503, status);
        Assert.Contains("plan_unavailable", body);
        Assert.Equal(0, _reliefCalls);
    }

    [Fact]
    public async Task Status_WithoutPlanner_AdvertisesLeaseAndRenewOnly()
    {
        var (_, body) = await Call("GET", "/api/status");
        Assert.Contains("\"capabilities\":[\"lease\",\"lease.renew\"]", body);
        Assert.DoesNotContain("lease.plan", body);               // 没接 planner 就不许声称支持
        Assert.Contains("maxSuspendCount", body);
    }

    // ---- v0.23.0 修偶发 400：body 落在第二个 TCP 段时必须仍被读到 ----

    [Fact]
    public async Task SplitPacket_BodyArrivesLater_StillParsed()
    {
        // 回归：旧实现只 ReadAsync 一次，body 分第二段到达时被丢掉 → 400 bad_json。
        // 实测 lease-demo.py 连跑 3 次命中 1 次；调用方会误判成参数错并放弃。
        const string body = """{"targetAvailableGb":6,"requester":"split"}""";
        var raw = await SendSplit("POST /api/lease", body);

        Assert.Contains("200", raw.Split("\r\n")[0]);
        Assert.Contains("\"state\":\"active\"", raw);
    }

    [Fact]
    public async Task SplitPacket_MultibyteRequester_IsNotMangled()
    {
        // 分段时按字节累积、只在完整 body 后解码 UTF-8，否则中文 requester 会被切成替换字符
        const string body = """{"targetAvailableGb":6,"requester":"中文调用方"}""";
        var raw = await SendSplit("POST /api/lease", body);

        var json = raw[(raw.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("中文调用方", doc.RootElement.GetProperty("requester").GetString());
    }

    /// <summary>先发 header、等一会再发 body，模拟 TCP 分段。</summary>
    private async Task<string> SendSplit(string requestLine, string body)
    {
        using var client = new TcpClient("127.0.0.1", _port);
        var stream = client.GetStream();
        var head = $"{requestLine} HTTP/1.1\r\nHost: 127.0.0.1\r\n" +
                   $"Authorization: Bearer {Token}\r\nContent-Type: application/json\r\n" +
                   $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.FlushAsync();
        await Task.Delay(80);                           // 让服务端先完成一次"只有 header"的读
        await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        await stream.FlushAsync();

        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        int n;
        while ((n = await stream.ReadAsync(buf)) > 0) ms.Write(buf, 0, n);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>
/// 租约 API 端口冲突（v0.10.0）。故障场景：陌生人第一次装上，端口恰好被别的程序占用，
/// 旧实现 `_listener.Start()` 直接抛 SocketException，一路传到 `async void Loaded`，
/// 弹一个"未处理异常"的模态框——而他根本不知道租约 API 是什么。
/// </summary>
public class LeaseApiPortConflictTests
{
    [Fact]
    public void Start_WhenPortTaken_ReturnsFalseAndRecordsError()
    {
        // 先占住一个端口
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        int taken = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            var mgr = new LeaseManager();
            using var api = new LeaseApiService(
                () => new SystemMemoryInfo { AvailablePhysicalBytes = 1L << 30, TotalPhysicalBytes = 16L << 30 },
                (_, _) => (new LeaseReliefResult(0, 0, ""), new List<LeaseSuspendedProcess>()),
                mgr,
                () => "t",
                () => taken);
            bool started = api.Start();   // 绝不抛

            Assert.False(started);
            Assert.False(api.Running);
            Assert.False(string.IsNullOrWhiteSpace(api.LastError));
            Assert.Contains(taken.ToString(), api.LastError);   // 报错里带端口号，用户能定位
        }
        finally { blocker.Stop(); }
    }

    [Fact]
    public void Start_Success_LeavesNoError()
    {
        var mgr = new LeaseManager();
        using var api = new LeaseApiService(
            () => new SystemMemoryInfo { AvailablePhysicalBytes = 1L << 30, TotalPhysicalBytes = 16L << 30 },
            (_, _) => (new LeaseReliefResult(0, 0, ""), new List<LeaseSuspendedProcess>()),
            mgr, () => "t", () => 0);

        Assert.True(api.Start());
        Assert.True(api.Running);
        Assert.Equal("", api.LastError);
    }
}
