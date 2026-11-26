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
    /// <summary>零动作预演（v0.23.0）。null = App 层没接，`POST /api/plan` 返回 503 而不是假装成功。</summary>
    private readonly Func<LeaseRequest, LeasePolicy, LeasePlan>? _planExecutor;

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
    /// <summary>
    /// 上次启动失败的原因（空 = 正常）。v0.10.0 新增：
    /// 旧实现 `_listener.Start()` 不捕获异常，端口被占时异常会一路抛到 `async void Loaded`，
    /// 给第一次启动的陌生用户弹一个"未处理异常"模态框——而他根本不知道租约 API 是什么。
    /// </summary>
    public string LastError { get; private set; } = "";
    /// <summary>实际监听端口（测试用 port=0 时由它回读）。</summary>
    public int BoundPort { get; private set; }

    public LeaseApiService(
        Func<SystemMemoryInfo> mem,
        Func<LeaseRequest, LeasePolicy, (LeaseReliefResult relief, List<LeaseSuspendedProcess> suspended)> reliefExecutor,
        LeaseManager manager,
        Func<string> token,
        Func<int> port,
        Func<LeaseRequest, LeasePolicy, LeasePlan>? planExecutor = null)
    {
        _mem = mem;
        _reliefExecutor = reliefExecutor;
        _manager = manager; _token = token; _port = port;
        _planExecutor = planExecutor;
    }

    /// <summary>
    /// 启动监听。返回 false 表示没起来（端口占用等），原因见 <see cref="LastError"/>。
    /// **绝不抛异常**——调用方在 UI 加载路径上，异常会变成模态崩溃框。
    /// </summary>
    public bool Start()
    {
        Stop();
        LastError = "";
        int port = 0;
        try
        {
            port = _port();
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var ct = _cts.Token;
            Task.Run(() => AcceptLoopAsync(ct), ct);
            return true;
        }
        catch (SocketException ex)
        {
            LastError = $"端口 {port} 无法监听（{(ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? "已被其他程序占用" : ex.SocketErrorCode.ToString())}）";
            break_down();
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"租约 API 启动失败：{ex.Message}";
            break_down();
            return false;
        }

        void break_down()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }
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

    /// <summary>请求体上限：超过按 400 处理，避免恶意客户端把内存吃满。</summary>
    private const int MaxRequestBytes = 1024 * 1024;

    private static async Task<(string method, string path, string body, string auth)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        int headEnd = -1;
        int contentLength = 0;

        // v0.23.0 修偶发 400：旧实现只 ReadAsync 一次，假设 header 与 body 在同一个
        // TCP 段里到达。实测约 1/3 的请求 body 落在第二次读（lease-demo.py 连跑 3 次复现
        // 1 次），于是 Deserialize("") 抛异常 → 400 bad_json——调用方会误判成"参数错"
        // 并放弃重试。改为按 Content-Length 读满；header 用 ASCII 解、body 用 UTF-8 解，
        // 避免分段解码切断中文 requester 的多字节字符。
        while (ms.Length < MaxRequestBytes)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) break;
            ms.Write(buffer, 0, read);

            if (headEnd < 0)
            {
                headEnd = IndexOfHeaderEnd(ms.GetBuffer(), (int)ms.Length);
                if (headEnd >= 0)
                    contentLength = ParseContentLength(ms.GetBuffer(), headEnd);
            }
            if (headEnd >= 0 && ms.Length >= headEnd + 4 + contentLength) break;
        }

        var all = ms.GetBuffer();
        int total = (int)ms.Length;
        var head = headEnd < 0
            ? Encoding.ASCII.GetString(all, 0, total)
            : Encoding.ASCII.GetString(all, 0, headEnd);

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

        int bodyStart = headEnd < 0 ? total : Math.Min(total, headEnd + 4);
        int bodyLen = Math.Max(0, total - bodyStart);
        var body = bodyLen > 0 ? Encoding.UTF8.GetString(all, bodyStart, bodyLen) : "";
        return (method, path, body, auth);
    }

    /// <summary>找 header 结束的 \r\n\r\n，返回其起始下标（找不到 -1）。</summary>
    private static int IndexOfHeaderEnd(byte[] buf, int length)
    {
        for (int i = 0; i + 3 < length; i++)
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        return -1;
    }

    /// <summary>从 header 文本里取 Content-Length（缺失/非法视为 0，钳到上限）。</summary>
    private static int ParseContentLength(byte[] buf, int headEnd)
    {
        var head = Encoding.ASCII.GetString(buf, 0, headEnd);
        foreach (var line in head.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line["Content-Length:".Length..].Trim(), out var n) && n > 0)
                return Math.Min(n, MaxRequestBytes);
        }
        return 0;
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
                // v0.23.0：能力发现。agent 先看这个再决定调什么，避免"试了才知道不支持"。
                capabilities = _planExecutor == null
                    ? new[] { "lease", "lease.renew" }
                    : new[] { "lease", "lease.renew", "lease.plan" },
                limits = new
                {
                    minTargetGb = 0.5,
                    maxTtlSeconds = LeaseManager.MaxTtlSeconds,
                    maxSuspendCount = LeaseHardPlanner.MaxSuspendCount
                },
                lease
            }, Json));
        }

        if (method == "POST" && path == "/api/plan")
        {
            LeaseRequest? planReq;
            try { planReq = JsonSerializer.Deserialize<LeaseRequest>(body, Json); }
            catch { return (400, """{"error":"bad_json"}"""); }
            if (planReq == null || planReq.TargetAvailableGb <= 0)
                return (400, """{"error":"targetAvailableGb must be > 0"}""");
            if (planReq.RequesterPid < 0)
                return (400, """{"error":"requesterPid must be >= 0"}""");
            if (_planExecutor == null)
                return (503, """{"error":"plan_unavailable"}""");   // 不假装成功

            var planPolicy = string.Equals(planReq.Policy, "hard", StringComparison.OrdinalIgnoreCase)
                ? LeasePolicy.Hard : LeasePolicy.Soft;
            var plan = _planExecutor(planReq, planPolicy);
            return (200, JsonSerializer.Serialize(plan, Json));
        }

        if (method == "POST" && path == "/api/lease")
        {
            LeaseRequest? req;
            try { req = JsonSerializer.Deserialize<LeaseRequest>(body, Json); }
            catch { return (400, """{"error":"bad_json"}"""); }
            if (req == null || req.TargetAvailableGb <= 0)
                return (400, """{"error":"targetAvailableGb must be > 0"}""");
            // v0.23.0：负 PID 无意义；0 表示"未指定"（兼容旧客户端）。
            if (req.RequesterPid < 0)
                return (400, """{"error":"requesterPid must be >= 0"}""");

            var policy = string.Equals(req.Policy, "hard", StringComparison.OrdinalIgnoreCase)
                ? LeasePolicy.Hard : LeasePolicy.Soft;

            // v0.9.0：先原子占位再执行腾挪。旧顺序（检查→腾挪→Acquire）在并发下会让
            // 第二个请求的 relief 已经挂起进程、却因 Acquire 失败而无人记账 → 那些进程永不恢复。
            var reserved = _manager.TryReserve(req, now);
            if (reserved == null)
            {
                // v0.23.0：409 不再是死胡同——告诉等待方"谁占着、还要多久"。
                // 排队逻辑放在客户端（MCP）：服务端长轮询会引入"租约授予了但客户端已超时断开"
                // 的僵尸租约，而客户端重试的话，进程一死就自然停止。
                var holder = _manager.Current(now);
                int retryAfter = 5;
                if (holder != null)
                {
                    // 不是"还剩多久就等多久"——对方可能随时提前归还。
                    // 取剩余时间的 1/4 并封顶 10s：长租约也不至于让人干等 30 秒。
                    double remaining = (holder.ExpiresUtc - now).TotalSeconds;
                    retryAfter = Math.Clamp((int)Math.Ceiling(remaining / 4.0), 1, 10);
                }
                return (409, JsonSerializer.Serialize(new
                {
                    error = "lease_active",
                    holder = holder == null ? null : new
                    {
                        requester = holder.Requester,
                        expiresUtc = holder.ExpiresUtc,
                        remainingSeconds = Math.Round(Math.Max(0, (holder.ExpiresUtc - now).TotalSeconds), 1),
                        achievedAvailableGb = holder.AchievedAvailableGb
                    },
                    retryAfterSeconds = retryAfter
                }, Json));
            }

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

        if (method == "POST" && path.StartsWith("/api/lease/") && path.EndsWith("/renew", StringComparison.OrdinalIgnoreCase))
        {
            var renewId = path["/api/lease/".Length..^"/renew".Length];
            if (string.IsNullOrEmpty(renewId)) return (404, """{"error":"not_found"}""");

            LeaseRequest? renewReq;
            try { renewReq = JsonSerializer.Deserialize<LeaseRequest>(body, Json); }
            catch { return (400, """{"error":"bad_json"}"""); }

            var (renewed, justExpired) = _manager.Renew(renewId, renewReq?.TtlSeconds ?? 0, now);
            if (justExpired)
                // 410 与 404 分开：410 告诉调用方"这个租约存在过、已经过期、只能重申请"
                return (410, """{"error":"expired"}""");
            return renewed == null
                ? (404, """{"error":"not_found_or_expired"}""")
                : (200, JsonSerializer.Serialize(renewed, Json));
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
        409 => "Conflict", 410 => "Gone", 500 => "Internal Server Error", 503 => "Service Unavailable",
        _ => status.ToString()
    };
}
