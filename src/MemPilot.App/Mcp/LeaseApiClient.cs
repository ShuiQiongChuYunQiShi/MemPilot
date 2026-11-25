using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MemPilot.App.Mcp;

/// <summary>
/// MCP 层对租约 API 的最小依赖面。抽接口只为一件事：协议层可以被单测（Fake 返回预设响应），
/// 不用为了测 tools/call 起一个真 HTTP 服务。
/// </summary>
internal interface ILeaseApi
{
    bool HasToken { get; }
    Task<LeaseApiClient.Result> CallAsync(HttpMethod method, string path, string? json = null,
        CancellationToken ct = default);
    string Explain(LeaseApiClient.Result result);
}

/// <summary>
/// 租约 API 的最小 HTTP 客户端（MCP server 专用）。
///
/// 架构约束：MCP server **只能当客户端**。租约账本（LeaseManager + 挂起列表）只活在常驻 GUI
/// 进程里；如果 MCP 进程自己腾内存，归还时 RestoreHook 不会触发，挂起的进程永不恢复——
/// 这正是 v0.9.0 修过的"无人记账的挂起"。所以这里只发 HTTP，绝不引用 Core 的处置原语。
///
/// 代价：MCP 需要 GUI 在跑、租约 API 已启用。这是有意的——它换来的是一条账本。
/// </summary>
internal sealed class LeaseApiClient : ILeaseApi
{
    public const int DefaultPort = 52717;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);   // hard 腾挪可能较慢

    public sealed record Result(int Status, string Body)
    {
        public bool Ok => Status >= 200 && Status < 300;
    }

    private readonly HttpClient _http = new(new HttpClientHandler
    {
        // 本地回环绝不走系统代理：本机代理（FlClash 7890）时开时关，一旦 HttpClient
        // 继承系统代理而代理没开，mem_status 会一直等到超时——表现为"MCP 卡住"。
        UseProxy = false
    })
    { Timeout = Timeout };
    private readonly string _dataDir;

    public string Token { get; }
    public int Port { get; }
    public bool HasToken => !string.IsNullOrEmpty(Token);

    public LeaseApiClient()
    {
        _dataDir = App.ResolveDataDir();
        Token = ReadToken(_dataDir);
        Port = ReadPort(_dataDir);
        McpServer.Trace($"client ready: port={Port} tokenLen={Token.Length} dir={_dataDir}");
    }

    /// <summary>只读令牌——绝不生成。MCP 若自己生成一个，就不等于 GUI 正在用的那个。</summary>
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
        catch { /* 配置损坏不该让 MCP 起不来 */ }
        return DefaultPort;
    }

    public async Task<Result> CallAsync(HttpMethod method, string path, string? json = null,
        CancellationToken ct = default)
    {
        try
        {
            McpServer.Trace($"-> {method} {path}");
            using var req = new HttpRequestMessage(method, $"http://127.0.0.1:{Port}{path}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            if (json != null)
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            McpServer.Trace($"<- {(int)resp.StatusCode} ({body.Length} chars)");
            return new Result((int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            McpServer.Trace($"!! {method} {path}: {ex.GetType().Name}: {ex.Message}");
            // 连接失败统一成 status 0：调用方据此区分"服务不在"与"服务拒绝了请求"
            return new Result(0, ex.Message);
        }
    }

    /// <summary>把失败翻译成模型能据以行动的一句话（而不是一个 HTTP 码）。</summary>
    public string Explain(Result result) => Explain(result, Port, _dataDir);

    /// <summary>静态版本：单测不需要构造真实客户端（避免依赖 %APPDATA% 的现存内容）。</summary>
    internal static string Explain(Result r, int port, string dataDir) => r.Status switch
    {
        0 => $"Cannot reach MemPilot at 127.0.0.1:{port}. Is MemPilot running, and is the " +
             "lease API enabled? (Settings -> 租约 API). Token file: " +
             Path.Combine(dataDir, "lease-token.txt"),
        401 => "Unauthorized: the bearer token does not match. Open MemPilot Settings -> 租约 API, " +
               "regenerate the token, then restart this MCP server.",
        409 => $"A lease is already active. {Trimmed(r.Body)}",
        404 => $"Not found: {Trimmed(r.Body)}",
        400 => $"Bad request: {Trimmed(r.Body)}",
        503 => "Plan capability is not wired in this build (App did not provide a planner).",
        _ => $"HTTP {r.Status}: {Trimmed(r.Body)}"
    };

    private static string Trimmed(string body) =>
        string.IsNullOrWhiteSpace(body) ? "(empty body)" : body.Length > 400 ? body[..400] + "…" : body;
}
