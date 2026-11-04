using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MemPilot.Models;

namespace MemPilot.Services;

/// <summary>
/// 租约 API（v0.7.0 M1）：本地 HTTP 服务，AI 任务在开跑前申请内存、结束后归还。
///
/// 安全边界：只绑 127.0.0.1 + Bearer 令牌（令牌落盘 lease-token.txt，设置页可见/可重新生成）。
/// 不用 HttpListener（要 URLACL）也不上 ASP.NET——TcpListener 手写最小 HTTP，
/// 应答一律 Connection: close，四个端点够用且零依赖。
///
/// 端点（全部要求 Authorization: Bearer &lt;token&gt;）：
///   GET    /api/status      → 版本/内存/提交/是否有活跃租约
///   POST   /api/lease       → 申请 {targetAvailableGb, ttlSeconds, requester}；单活跃，冲突 409
///   GET    /api/lease       → 当前租约状态
///   DELETE /api/lease/{id}  → 归还
///
/// 软策略只做：清低优先级备用列表 + 压缩"安全子集"（绝不碰工作/游戏/前台/忙碌进程），
/// 返回**实测达成量**而非承诺量；显存无法凭空释放，/api/status 如实报告当前显存。
/// </summary>
public sealed class LeaseApiService : IDisposable
{
    private readonly Func<SystemMemoryInfo> _mem;
    private readonly Func<LeaseRequest, LeasePolicy, (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended)> _reliefExecutor;
    private readonly LeaseManager _manager;
    private readonly Func<string> _token;
    private readonly Func<int> _port;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 对外 JSON 一律 camelCase（调用方是 AI 脚本，习惯 JS 风格）。
    /// PropertyNameCaseInsensitive 必须开：否则请求里的 "policy"/"targetAvailableGb" 匹配不到属性，
    /// 会静默回落成默认值（v0.8.0 实测踩到：hard 策略被解析成 soft）。
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public LeaseManager Manager => _manager;
    public bool Running => _listener != null;
    /// <summary>实际监听端口（测试用 port=0 时由它回读）。</summary>
    public int BoundPort { get; private set; }

    public LeaseApiService(
        Func<SystemMemoryInfo> mem,
        Func<LeaseRequest, LeasePolicy, (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended)> reliefExecutor,
        LeaseManager manager,
        Func<string> token,
        Func<int> port)
    {
        _mem = mem;
        _reliefExecutor = reliefExecutor;
        _manager = manager; _token = token; _port = port;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port());
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var ct = _cts.Token;
        Task.Run(() => AcceptLoopAsync(ct), ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            var (method, path, body, auth) = await ReadRequestAsync(stream, ct);
            var (status, payload) = Route(method, path, body, auth);
            await WriteResponseAsync(stream, status, payload, ct);
        }
        catch { /* 断连/超时：下一个请求继续 */ }
    }

    private static async Task<(string method, string path, string body, string auth)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        int read = await stream.ReadAsync(buffer, ct);
        var raw = Encoding.UTF8.GetString(buffer, 0, read);
        var headEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = headEnd < 0 ? raw : raw[..headEnd];
        var lines = head.Split("\r\n");
        var first = lines.FirstOrDefault() ?? "";
        var parts = first.Split(' ');
        var method = parts.Length > 0 ? parts[0] : "";
        var rawPath = parts.Length > 1 ? parts[1] : "/";
        var path = rawPath.TrimEnd('/');
        if (path.Length == 0) path = "/";
        string auth = "";
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                auth = line["Authorization:".Length..].Trim();
        }
        var body = headEnd >= 0 && raw.Length > headEnd + 4 ? raw[(headEnd + 4)..] : "";
        return (method, path, body, auth);
    }

    private (int status, string json) Route(string method, string path, string body, string authHeader)
    {
        if (!path.StartsWith("/api", StringComparison.Ordinal))
            return (404, """{"error":"not_found"}""");
        if (authHeader != "Bearer " + _token())
            return (401, """{"error":"unauthorized"}""");

        var now = DateTime.UtcNow;

        if (method == "GET" && path == "/api/status")
        {
            var mem = _mem();
            var lease = _manager.Current(now);
            double? cf = mem.TotalCommitLimitBytes > 0
                ? (mem.TotalCommitLimitBytes - mem.TotalCommittedBytes) * 100.0 / mem.TotalCommitLimitBytes : null;
            return (200, JsonSerializer.Serialize(new
            {
                version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "",
                availableGb = Math.Round(mem.AvailablePhysicalBytes / 1073741824.0, 2),
                totalGb = Math.Round(mem.TotalPhysicalBytes / 1073741824.0, 2),
                commitFreePct = cf is { } v ? Math.Round(v, 1) : (double?)null,
                leaseActive = lease != null,
                lease
            }, Json));
        }

        if (method == "POST" && path == "/api/lease")
        {
            LeaseRequest? req;
            try { req = JsonSerializer.Deserialize<LeaseRequest>(body, Json); }
            catch { return (400, """{"error":"bad_json"}"""); }
            if (req == null || req.TargetAvailableGb <= 0)
                return (400, """{"error":"targetAvailableGb must be > 0"}""");

            var policy = string.Equals(req.Policy, "hard", StringComparison.OrdinalIgnoreCase)
                ? LeasePolicy.Hard : LeasePolicy.Soft;

            // v0.9.0：先原子占位再执行腾挪。旧顺序（检查→腾挪→Acquire）在并发下会让
            // 第二个请求的 relief 已经挂起进程、却因 Acquire 失败而无人记账 → 那些进程永不恢复。
            var reserved = _manager.TryReserve(req, now);
            if (reserved == null)
                return (409, """{"error":"lease_active"}""");

            (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended) result;
            try { result = _reliefExecutor(req, policy); }
            catch (Exception ex)
            {
                _manager.AbortReservation(reserved.Id);
                return (500, JsonSerializer.Serialize(new { error = "relief_failed", detail = ex.Message }, Json));
            }

            var lease = _manager.CompleteReservation(reserved.Id, result.relief, DateTime.UtcNow, policy, result.suspended);
            return lease == null
                ? (500, """{"error":"reservation_lost"}""")
                : (200, JsonSerializer.Serialize(lease, Json));
        }

        if (method == "GET" && path == "/api/lease")
        {
            var lease = _manager.Current(now);
            return lease == null ? (404, """{"error":"no_lease"}""") : (200, JsonSerializer.Serialize(lease, Json));
        }

        if (method == "DELETE" && path.StartsWith("/api/lease/"))
        {
            var id = path["/api/lease/".Length..];
            var released = _manager.Release(id, now);
            return released == null ? (404, """{"error":"not_found_or_expired"}""") : (200, JsonSerializer.Serialize(released, Json));
        }

        return (404, """{"error":"not_found"}""");
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found",
        409 => "Conflict", 500 => "Internal Server Error", _ => status.ToString()
    };
}
