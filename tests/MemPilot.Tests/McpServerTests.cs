using System.Net.Http;
using System.Text.Json;
using MemPilot.App.Mcp;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// MCP 协议层（v0.23.0）。不测传输（stdio/HTTP）——测的是"喂一行 JSON 出来一条合法响应"：
/// 通知不回复、未知方法给 JSON-RPC 错误、tool 失败给 isError 而不是协议错误、
/// 以及那条安全默认值（requesterPid 缺省 = 本进程）。
/// </summary>
public class McpServerTests
{
    private sealed class FakeApi : ILeaseApi
    {
        public bool HasToken => true;
        public List<(string Method, string Path, string? Body)> Calls { get; } = new();

        public Func<string, string, string?, LeaseHttp.Result> Handler { get; set; } =
            (_, _, _) => new LeaseHttp.Result(200, "{}");

        public Task<LeaseHttp.Result> CallAsync(HttpMethod method, string path, string? json = null,
            CancellationToken ct = default)
        {
            Calls.Add((method.Method, path, json));
            return Task.FromResult(Handler(method.Method, path, json));
        }

        public string Explain(LeaseHttp.Result r) => $"explained:{r.Status}";
    }

    private static string? Handle(FakeApi api, string json) =>
        McpServer.HandleAsync(json, api).GetAwaiter().GetResult();

    private static string CallTool(FakeApi api, string name, string argsJson)
    {
        // 用拼接而不是内插原始字符串：JSON 的右括号和插值定界符会打架（CS9007）
        var request = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" +
                      name + "\",\"arguments\":" + argsJson + "}}";
        return Handle(api, request) ?? throw new Xunit.Sdk.XunitException("tools/call must always answer");
    }

    // ---- 协议骨架 ----

    [Fact]
    public void Initialize_DeclaresToolsCapability()
    {
        var resp = Handle(new FakeApi(), """{"jsonrpc":"2.0","id":1,"method":"initialize"}""")!;
        Assert.Contains("\"protocolVersion\": \"2024-11-05\"", resp);
        Assert.Contains("\"tools\"", resp);
        Assert.Contains("mempilot", resp);
    }

    [Fact]
    public void Notification_IsNeverAnswered()
    {
        // 通知没有 id；回了就是协议违规，客户端会当成重复响应
        Assert.Null(Handle(new FakeApi(), """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(Handle(new FakeApi(), """{"jsonrpc":"2.0","method":"notifications/cancelled"}"""));
    }

    [Fact]
    public void ToolsList_ExposesExactlyThreeTools()
    {
        var resp = Handle(new FakeApi(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")!;
        Assert.Contains("\"mem_status\"", resp);
        Assert.Contains("\"mem_plan\"", resp);
        Assert.Contains("\"mem_lease\"", resp);
        // 工具集刻意保持小：加 tool 要先回答"它替代了哪一次往返"
        Assert.Equal(3, CountOccurrences(resp, "\"inputSchema\""));
    }

    [Fact]
    public void UnknownMethod_ReturnsJsonRpcError()
    {
        var resp = Handle(new FakeApi(), """{"jsonrpc":"2.0","id":3,"method":"does/not/exist"}""")!;
        Assert.Contains("-32601", resp);
    }

    [Fact]
    public void BadJson_ReturnsParseError()
    {
        var resp = Handle(new FakeApi(), "{not json")!;
        Assert.Contains("-32700", resp);
    }

    [Fact]
    public void Ping_ReturnsEmptyResult()
    {
        var resp = Handle(new FakeApi(), """{"jsonrpc":"2.0","id":4,"method":"ping"}""")!;
        Assert.Contains("\"result\": {}", resp);
    }

    // ---- tools/call ----

    [Fact]
    public async Task Status_ForwardsToApi_AndReturnsText()
    {
        var api = new FakeApi { Handler = (_, _, _) => new(200, """{"availableGb":12.5,"leaseActive":false}""") };
        var resp = await McpServer.HandleAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"mem_status","arguments":{}}}""", api);

        Assert.Contains("availableGb", resp);
        Assert.Contains("\"isError\": false", resp);
        Assert.Equal("GET /api/status", api.Calls[0].Method + " " + api.Calls[0].Path);
    }

    [Fact]
    public void Status_WhenUnreachable_IsToolError_NotProtocolError()
    {
        var api = new FakeApi { Handler = (_, _, _) => new(0, "connection refused") };
        var resp = CallTool(api, "mem_status", "{}");

        Assert.Contains("\"isError\": true", resp);
        Assert.Contains("explained:0", resp);          // 失败原因走 Explain，模型能据此行动
        Assert.DoesNotContain("-32603", resp);         // 不是协议错误
    }

    [Fact]
    public void Plan_DefaultsRequesterPidToThisProcess()
    {
        // 安全默认值：模型不传 requesterPid 时，至少排除 MCP 自己（它通常是 agent 的子进程）
        var api = new FakeApi { Handler = (_, _, _) => new(200, """{"feasible":true}""") };
        CallTool(api, "mem_plan", """{"targetAvailableGb":8,"policy":"hard"}""");

        var body = api.Calls.Single().Body!;
        Assert.Contains($"\"requesterPid\":{Environment.ProcessId}", body);
        Assert.Contains("\"policy\":\"hard\"", body);
    }

    [Fact]
    public void Plan_WhenSamplingIsCold_PrependsRetryHint()
    {
        // 刚启动时 inUse 虚高、候选为空；不提示的话模型会得出"这台机器没余量"的错误结论
        var api = new FakeApi
        {
            Handler = (_, _, _) => new(200, """{"feasible":false,"samplingWarm":false,"wouldSuspend":[]}""")
        };
        var resp = CallTool(api, "mem_plan", """{"targetAvailableGb":8}""");

        Assert.Contains("sampling is not warm", resp);
        Assert.Contains("Retry in about 30 seconds", resp);
    }

    [Fact]
    public void Plan_WhenWarm_HasNoRetryHint()
    {
        var api = new FakeApi { Handler = (_, _, _) => new(200, """{"feasible":true,"samplingWarm":true}""") };
        var resp = CallTool(api, "mem_plan", """{"targetAvailableGb":8}""");
        Assert.DoesNotContain("not warm", resp);
    }

    [Fact]
    public void Plan_ForwardsDenyNames()
    {
        var api = new FakeApi { Handler = (_, _, _) => new(200, """{"feasible":true}""") };
        CallTool(api, "mem_plan", """{"targetAvailableGb":8,"denyNames":["Weixin","WeChatAppEx"]}""");

        var body = api.Calls.Single().Body!;
        Assert.Contains("\"denyNames\":[\"Weixin\",\"WeChatAppEx\"]", body);
    }

    [Fact]
    public void Plan_RequiresPositiveTarget()
    {
        var api = new FakeApi();
        Assert.Contains("\"isError\": true", CallTool(api, "mem_plan", """{"targetAvailableGb":0}"""));
        Assert.Empty(api.Calls);                        // 参数不合法就不该打扰服务端
    }

    [Fact]
    public void Plan_WhenServerLacksCapability_IsToolError()
    {
        var api = new FakeApi { Handler = (_, _, _) => new(404, """{"error":"not_found"}""") };
        var resp = CallTool(api, "mem_plan", """{"targetAvailableGb":4}""");
        Assert.Contains("\"isError\": true", resp);
        Assert.Contains("explained:404", resp);
    }

    [Fact]
    public void Acquire_ForwardsPolicyTtlAndRequester()
    {
        var api = new FakeApi
        {
            Handler = (_, _, _) => new(200,
                """{"id":"abc123","targetAvailableGb":12,"achievedAvailableGb":12,"shortfallGb":0,"suspended":[]}""")
        };
        var resp = CallTool(api, "mem_lease",
            """{"action":"acquire","targetAvailableGb":12,"ttlSeconds":900,"policy":"hard","requester":"claude-code"}""");

        var body = api.Calls.Single().Body!;
        Assert.Contains("\"ttlSeconds\":900", body);
        Assert.Contains("\"policy\":\"hard\"", body);
        Assert.Contains("\"requester\":\"claude-code\"", body);
        Assert.Contains("achieved 12.0 GB of 12.0 GB", resp);
        Assert.Contains("\"isError\": false", resp);
    }

    [Fact]
    public void Acquire_WithShortfall_SaysTheJobMayNotFit()
    {
        var api = new FakeApi
        {
            Handler = (_, _, _) => new(200,
                """{"id":"abc","targetAvailableGb":12,"achievedAvailableGb":8.2,"shortfallGb":3.8,"suspended":[]}""")
        };
        var resp = CallTool(api, "mem_lease", """{"action":"acquire","targetAvailableGb":12}""");
        Assert.Contains("shortfall 3.8 GB", resp);
        Assert.Contains("may not fit", resp);           // 不能让模型以为"申请成功 = 够用"
    }

    [Fact]
    public void Release_WithoutId_FetchesActiveLeaseThenDeletesIt()
    {
        var api = new FakeApi
        {
            Handler = (method, path, _) => method == "GET"
                ? new(200, """{"id":"abc123","state":"active"}""")
                : new(200, """{"id":"abc123","state":"released","suspended":[{"restored":true},{"restored":true}]}""")
        };
        var resp = CallTool(api, "mem_lease", """{"action":"release"}""");

        Assert.Equal(2, api.Calls.Count);
        Assert.Equal("GET", api.Calls[0].Method);
        Assert.Equal("DELETE", api.Calls[1].Method);
        Assert.Equal("/api/lease/abc123", api.Calls[1].Path);
        Assert.Contains("restored: 2/2", resp);
    }

    [Fact]
    public void Release_WithNoActiveLease_IsFriendlyNotAnError()
    {
        var api = new FakeApi { Handler = (_, _, _) => new(404, """{"error":"no_lease"}""") };
        var resp = CallTool(api, "mem_lease", """{"action":"release"}""");
        Assert.Contains("No active lease", resp);
        Assert.Contains("\"isError\": false", resp);    // "没有可归还的"不是失败
    }

    [Fact]
    public void Acquire_ConflictWithoutWait_FailsImmediately_WithHolder()
    {
        var api = new FakeApi
        {
            Handler = (_, _, _) => new(409,
                """{"error":"lease_active","holder":{"requester":"other","remainingSeconds":30,"achievedAvailableGb":8}}""")
        };
        var resp = CallTool(api, "mem_lease", """{"action":"acquire","targetAvailableGb":6}""");

        Assert.Contains("\"isError\": true", resp);
        Assert.Contains("held by", resp);
        Assert.Contains("other", resp);
        Assert.Single(api.Calls);                       // 没传 waitSeconds 就不重试
    }

    [Fact]
    public void Acquire_WithWaitSeconds_RetriesUntilAvailable()
    {
        int attempts = 0;
        var api = new FakeApi
        {
            Handler = (_, _, _) =>
            {
                attempts++;
                return attempts < 2
                    ? new(409, """{"error":"lease_active","holder":{"requester":"other","remainingSeconds":1,"achievedAvailableGb":8},"retryAfterSeconds":1}""")
                    : new(200, """{"id":"abc","targetAvailableGb":6,"achievedAvailableGb":6,"shortfallGb":0,"suspended":[]}""");
            }
        };
        var resp = CallTool(api, "mem_lease",
            """{"action":"acquire","targetAvailableGb":6,"waitSeconds":10}""");

        Assert.Equal(2, attempts);
        Assert.Contains("Waited", resp);                // 告诉模型"等过"，不是秒回
        Assert.Contains("\"isError\": false", resp);
    }

    [Fact]
    public void Acquire_WhenWaitExpires_ReportsTheHolder()
    {
        var api = new FakeApi
        {
            Handler = (_, _, _) => new(409,
                """{"error":"lease_active","holder":{"requester":"other","remainingSeconds":300,"achievedAvailableGb":8},"retryAfterSeconds":1}""")
        };
        var resp = CallTool(api, "mem_lease",
            """{"action":"acquire","targetAvailableGb":6,"waitSeconds":1}""");

        Assert.Contains("\"isError\": true", resp);
        Assert.Contains("Timed out after", resp);
        Assert.Contains("held by", resp);               // 超时也要说清谁占着
    }

    [Fact]
    public void Renew_WithoutId_FetchesActiveLeaseThenPosts()
    {
        var api = new FakeApi
        {
            Handler = (method, path, _) => method == "GET"
                ? new(200, """{"id":"abc123","state":"active"}""")
                : new(200, """{"id":"abc123","renewCount":1,"remainingSeconds":3000,"expiresUtc":"2026-11-25T12:00:00Z"}""")
        };
        var resp = CallTool(api, "mem_lease", """{"action":"renew","ttlSeconds":600}""");

        Assert.Equal(2, api.Calls.Count);
        Assert.Equal("GET", api.Calls[0].Method);
        Assert.Equal("POST", api.Calls[1].Method);
        Assert.Equal("/api/lease/abc123/renew", api.Calls[1].Path);
        Assert.Contains("\"ttlSeconds\":600", api.Calls[1].Body);
        Assert.Contains("renewed (#1)", resp);
        Assert.Contains("\"isError\": false", resp);
    }

    [Fact]
    public void Renew_AfterExpiry_IsToolError()
    {
        // 410 走 Explain：告诉模型"只能重新申请"，而不是让它反复续期一个死租约
        var api = new FakeApi { Handler = (_, _, _) => new(410, """{"error":"expired"}""") };
        var resp = CallTool(api, "mem_lease", """{"action":"renew","leaseId":"abc"}""");
        Assert.Contains("\"isError\": true", resp);
        Assert.Contains("explained:410", resp);
    }

    [Fact]
    public void Renew_WithNoActiveLease_IsFriendly()
    {
        var api = new FakeApi { Handler = (_, _, _) => new(404, """{"error":"no_lease"}""") };
        var resp = CallTool(api, "mem_lease", """{"action":"renew"}""");
        Assert.Contains("No active lease to renew", resp);
        Assert.Contains("\"isError\": false", resp);
    }

    [Fact]
    public void UnknownAction_IsToolError()
    {
        var api = new FakeApi();
        Assert.Contains("\"isError\": true", CallTool(api, "mem_lease", """{"action":"nuke"}"""));
    }

    [Fact]
    public void UnknownTool_IsToolError()
    {
        var api = new FakeApi();
        Assert.Contains("Unknown tool", CallTool(api, "mem_kill_everything", "{}"));
    }

    // ---- 错误文案 ----

    [Theory]
    [InlineData(0, "Cannot reach MemPilot")]
    [InlineData(401, "regenerate the token")]
    [InlineData(409, "already active")]
    [InlineData(410, "already expired")]
    [InlineData(503, "not wired")]
    public void Explain_TranslatesStatusesIntoActions(int status, string expected)
    {
        var text = LeaseHttp.Explain(new LeaseHttp.Result(status, "{}"), 52717, @"C:\data");
        Assert.Contains(expected, text);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
