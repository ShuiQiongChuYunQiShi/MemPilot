using System.Net.Http;
using System.Text;
using System.Text.Json;
using MemPilot.Models;
using MemPilot.Services;
using Xunit;

namespace MemPilot.Tests;

/// <summary>
/// `POST /api/plan`（v0.23.0）：零动作预演。
///
/// 关键断言不是"返回了什么"，而是"它没做什么"——relief 执行器在这里被写成"一调用就炸"，
/// plan 一旦走了处置路径，测试会立刻红。
/// </summary>
public class LeaseApiPlanTests : IDisposable
{
    private const string Token = "test-token-0123456789abcdef";
    private readonly LeaseManager _manager = new();
    private readonly LeaseApiService _api;
    private readonly int _port;
    private LeaseRequest? _lastReq;
    private LeasePolicy _lastPolicy = LeasePolicy.Soft;
    private int _reliefCalls;

    public LeaseApiPlanTests()
    {
        _api = new LeaseApiService(
            mem: () => new SystemMemoryInfo
            {
                AvailablePhysicalBytes = 10L << 30,
                TotalPhysicalBytes = 32L << 30,
                TotalCommitLimitBytes = 55L << 30,
                TotalCommittedBytes = 30L << 30
            },
            reliefExecutor: (req, policy) =>
            {
                // 计数而不是直接 throw：同 fixture 里要能测"plan 不触发 relief"，
                // 也要能测"紧接着的真申请仍然成功"（plan 没占位）
                _reliefCalls++;
                return (new LeaseReliefResult(10, 10, "fake relief"), new List<LeaseSuspendedProcess>());
            },
            manager: _manager,
            token: () => Token,
            port: () => 0,
            planExecutor: (req, policy) =>
            {
                _lastReq = req;
                _lastPolicy = policy;
                return new LeasePlan
                {
                    TargetAvailableGb = req.TargetAvailableGb,
                    CurrentAvailableGb = 10,
                    ProjectedAvailableGb = 10,
                    ShortfallGb = Math.Max(0, req.TargetAvailableGb - 10),
                    Feasible = req.TargetAvailableGb <= 10,
                    Policy = policy == LeasePolicy.Hard ? "hard" : "soft",
                    ProjectionBasis = "current-only"
                };
            });
        Assert.True(_api.Start());
        _port = _api.BoundPort;
    }

    public void Dispose() => _api.Dispose();

    private async Task<(int status, string body)> Call(string method, string path, string? body = null)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{_port}{path}");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plan_ReturnsPlannerResult()
    {
        var (status, body) = await Call("POST", "/api/plan",
            """{"targetAvailableGb":8,"policy":"hard","requester":"agent"}""");

        Assert.Equal(200, status);
        Assert.Contains("\"feasible\":true", body);
        Assert.Contains("\"projectionBasis\":\"current-only\"", body);
        Assert.NotNull(_lastReq);
        Assert.Equal(8, _lastReq!.TargetAvailableGb);
        Assert.Equal(LeasePolicy.Hard, _lastPolicy);
    }

    [Fact]
    public async Task Plan_CreatesNoLease_AndDoesNotOccupyTheSlot()
    {
        await Call("POST", "/api/plan", """{"targetAvailableGb":4}""");
        Assert.Null(_manager.Current(DateTime.UtcNow));    // 零动作：账本还是空的
        Assert.Equal(0, _reliefCalls);                     // 也绝不触发腾挪

        // 紧接着真申请必须成功——plan 若偷偷占位，这里会 409
        var (status, _) = await Call("POST", "/api/lease", """{"targetAvailableGb":4,"requester":"x"}""");
        Assert.Equal(200, status);
        Assert.Equal(1, _reliefCalls);
    }

    [Fact]
    public async Task Plan_TargetAboveCurrent_IsNotFeasible()
    {
        var (status, body) = await Call("POST", "/api/plan", """{"targetAvailableGb":20}""");
        Assert.Equal(200, status);
        Assert.Contains("\"feasible\":false", body);
        Assert.Contains("\"shortfallGb\":10", body);
    }

    [Fact]
    public async Task Plan_BadTarget_Is400()
    {
        Assert.Equal(400, (await Call("POST", "/api/plan", """{"targetAvailableGb":0}""")).status);
        Assert.Equal(400, (await Call("POST", "/api/plan", """{"requesterPid":-1,"targetAvailableGb":4}""")).status);
        Assert.Equal(400, (await Call("POST", "/api/plan", "{not json")).status);
    }

    [Fact]
    public async Task Status_WithPlanner_AdvertisesPlanCapability()
    {
        var (status, body) = await Call("GET", "/api/status");
        Assert.Equal(200, status);
        Assert.Contains("lease.plan", body);
        Assert.Contains("maxSuspendCount", body);
        Assert.Contains("maxTtlSeconds", body);
    }
}
