using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MemPilot.Services;

/// <summary>
/// 租约 API 的最小 HTTP 客户端（v0.25.0：从 `App.Mcp.LeaseApiClient` 提取到 Core）。
///
/// 为什么放在 Core：`mempilot lease run`（命令行模式、不启动 WPF）与 MCP server 必须是
/// **同一份实现**。端口/token 的发现逻辑、超时、错误文案只要有一处不同，就会出现
/// "CLI 能用但 MCP 不能用"这类很难排查的故障。
///
/// 架构约束不变：这只是 HTTP 客户端。租约账本（含挂起恢复）只活在常驻 GUI 进程里，
/// 客户端绝不直接处置进程——第二个账本会让进程被挂起后无人恢复。
/// </summary>
public sealed class LeaseHttp : ILeaseApi
{
    public const int DefaultPort = 52717;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);   // hard 腾挪可能较慢

    /// <summary>HTTP 结果。Status=0 表示连不上（与"服务端拒绝了请求"区分开）。</summary>
    public sealed record Result(int Status, string Body)
    {
        public bool Ok => Status is >= 200 and < 300;
    }

    private readonly HttpClient _http = new(new HttpClientHandler
    {
        // 本地回环绝不走系统代理：本机代理（FlClash 7890）时开时关，一旦继承系统代理，
        // 请求会一直等到超时——表现就是"MCP/CLI 卡住"。
        UseProxy = false
    })
    { Timeout = Timeout };

    public string DataDir { get; }
    public string Token { get; }
    public int Port { get; }
    public bool HasToken => !string.IsNullOrEmpty(Token);

    public LeaseHttp(string? dataDir = null, int? port = null, string? token = null)
    {
        DataDir = dataDir ?? MemPilotPaths.ResolveDataDir();
        Token = token ?? ReadToken(DataDir);
        Port = port ?? ReadPort(DataDir);
    }

    /// <summary>只读令牌——绝不生成。客户端若自己生成一个，就不等于 GUI 正在用的那个。</summary>
    public static string ReadToken(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, "lease-token.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>从 config.json 读端口（camelCase；兼容历史 PascalCase），失败回落默认值。</summary>
    public static int ReadPort(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, "config.json");
            if (!File.Exists(path)) return DefaultPort;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in new[] { "leaseApiPort", "LeaseApiPort" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el) &&
                    el.TryGetInt32(out var port) && port > 0)
                    return port;
            }
        }
        catch { /* 配置损坏不该让调用方起不来 */ }
        return DefaultPort;
    }

    public async Task<Result> CallAsync(HttpMethod method, string path, string? json = null,
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(method, $"http://127.0.0.1:{Port}{path}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            if (json != null)
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new Result((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            // 连接失败统一成 status 0：调用方据此区分"服务不在"与"服务拒绝了请求"
            return new Result(0, ex.Message);
        }
    }

    /// <summary>把失败翻译成可据以行动的一句话（而不是一个 HTTP 码）。</summary>
    public string Explain(Result result) => Explain(result, Port, DataDir);

    public static string Explain(Result r, int port, string dataDir) => r.Status switch
    {
        0 => $"Cannot reach MemPilot at 127.0.0.1:{port}. Is MemPilot running, and is the " +
             "lease API enabled? (Settings -> 租约 API). Token file: " +
             Path.Combine(dataDir, "lease-token.txt"),
        401 => "Unauthorized: the bearer token does not match. Open MemPilot Settings -> 租约 API, " +
               "regenerate the token, then retry.",
        409 => $"A lease is already active. {Trimmed(r.Body)}",
        410 => "That lease already expired and its suspended processes were restored. " +
               "Acquire a new lease instead of renewing.",
        404 => $"Not found: {Trimmed(r.Body)}",
        400 => $"Bad request: {Trimmed(r.Body)}",
        503 => "Plan capability is not wired in this build (App did not provide a planner).",
        _ => $"HTTP {r.Status}: {Trimmed(r.Body)}"
    };

    private static string Trimmed(string body) =>
        string.IsNullOrWhiteSpace(body) ? "(empty body)" : body.Length > 400 ? body[..400] + "…" : body;
}
