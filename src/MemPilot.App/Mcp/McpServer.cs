using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MemPilot.App.Mcp;

/// <summary>
/// 最小 MCP server（stdio 传输，newline-delimited JSON-RPC 2.0）。
///
/// 为什么手写而不是引 SDK：项目运行时只有 1 个 NuGet 包，官方 SDK 会把依赖树变成十几个，
/// 而这里只需要 initialize / tools/list / tools/call / ping 四个方法（约 300 行）。
///
/// 为什么不直接调 Core：租约账本只活在常驻 GUI 进程里，见 LeaseApiClient 的注释。
///
/// 异常纪律：任何异常都只写 stderr 或转成 isError 的 tool 结果，绝不抛给宿主——
/// 客户端把它当"tool 失败"处理，比当"server 崩了"更好恢复。
/// </summary>
internal static class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        // 本地 stdio，不经 HTML：中文原样输出，否则 details 里的中文全是 \uXXXX，
        // 模型和人都读不动。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Run()
    {
        // 必须在**脱离** SynchronizationContext 的线程上跑。
        // OnStartup 的 Dispatcher 线程带 DispatcherSynchronizationContext，同步等待
        // async（GetAwaiter().GetResult()）时，await 的 continuation 会被 Post 回那个
        // 正被阻塞的线程 → 经典死锁。实测表现：卡在第一个 HTTP 请求，10 秒内无任何输出。
        return Task.Run(RunAsync).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = true     // 客户端等的是即时响应，不能靠缓冲
            };
            var client = new LeaseApiClient();
            if (!client.HasToken)
                Log("no lease token found. Start MemPilot once, enable Settings -> 租约 API, then retry.");

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string? response;
                try { response = await HandleAsync(line, client).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log(ex.Message);
                    response = Error(null, -32603, "internal error: " + ex.Message);
                }
                if (response != null) writer.WriteLine(response);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log("fatal: " + ex.Message);
            return 1;
        }
    }

    /// <summary>出问题时才说话：缺令牌/fatal 走 Log，正常路径保持安静（MCP 客户端会显示 stderr）。</summary>
    private static void Log(string message)
    {
        try { Console.Error.WriteLine("[mempilot-mcp] " + message); } catch { /* 无 stderr 句柄 */ }
    }

    /// <summary>逐请求诊断。默认关闭——正常路径不产生 stderr 噪音；排查时设 MEMPILOT_MCP_DEBUG=1。</summary>
    internal static void Trace(string message)
    {
        if (!DebugEnabled) return;
        Log(message);
    }

    internal static readonly bool DebugEnabled =
        Environment.GetEnvironmentVariable("MEMPILOT_MCP_DEBUG") == "1";

    /// <summary>协议层入口。internal 供单测直接喂 JSON —— 不需要为了测 tools/call 起真 HTTP。</summary>
    internal static async Task<string?> HandleAsync(string line, ILeaseApi client)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { return Error(null, -32700, "parse error"); }
        if (node is not JsonObject msg) return Error(null, -32600, "invalid request");

        var id = msg["id"];
        var method = msg["method"]?.GetValue<string>() ?? "";
        // 通知（notifications/initialized、notifications/cancelled…）没有 id，绝不能回复
        var isNotification = id == null;

        JsonNode? result = method switch
        {
            "initialize" => Initialize(),
            "tools/list" => ToolsList(),
            "tools/call" => await ToolsCallAsync(msg["params"], client),
            "ping" => new JsonObject(),
            _ => null
        };

        if (isNotification) return null;
        return result == null
            ? Error(id, -32601, $"method not found: {method}")
            : Response(id!, result);
    }

    private static string Response(JsonNode id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result
    }.ToJsonString(PrettyJson);

    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    }.ToJsonString(PrettyJson);

    private static string Version() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    private static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "mempilot", ["version"] = Version() },
        ["instructions"] =
            "MemPilot is a Windows memory pilot. Before starting a large job (model load, video " +
            "generation, big test run), call mem_plan to see whether the memory can be freed " +
            "WITHOUT touching any process. Then call mem_lease with action=acquire and release it " +
            "with the returned id. acquired leases report the amount ACTUALLY freed " +
            "(achievedAvailableGb), never a promise. Pass requesterPid (the PID of your own task " +
            "process) so MemPilot never suspends your process tree."
    };

    private static JsonNode Schema(string json) => JsonNode.Parse(json)!;

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "mem_status",
                ["description"] =
                    "Current memory state of the machine and the lease API's capabilities. " +
                    "Call this first: it tells you the port is reachable, whether an active lease " +
                    "exists, and which lease features this build supports.",
                ["inputSchema"] = Schema("""
                {"type":"object","properties":{},"additionalProperties":false}
                """)
            },
            new JsonObject
            {
                ["name"] = "mem_plan",
                ["description"] =
                    "Preview a memory lease WITHOUT touching any process. Returns measured current " +
                    "available memory, an upper-bound projection, which processes WOULD be " +
                    "suspended, and how many processes the safety rules exclude (and why). Use it " +
                    "to decide whether to start a large job. projectionBasis tells you how the " +
                    "projection was derived: 'hard-upper-bound' (may suspend idle processes) or " +
                    "'current-only' (no assumptions; a lower bound).",
                ["inputSchema"] = Schema("""
                {
                  "type":"object",
                  "properties":{
                    "targetAvailableGb":{"type":"number","description":"How much available memory the job needs, in GB."},
                    "policy":{"type":"string","enum":["soft","hard"],"description":"soft = purge standby + trim idle working sets only. hard = also suspend up to 8 idle processes (restored on release). Default soft."},
                    "requester":{"type":"string","description":"Who is asking (written to the decision log), e.g. 'claude-code'."},
                    "requesterPid":{"type":"integer","description":"PID of your own task process. That process and its whole subtree will never be suspended."}
                  },
                  "required":["targetAvailableGb"],
                  "additionalProperties":false
                }
                """)
            },
            new JsonObject
            {
                ["name"] = "mem_lease",
                ["description"] =
                    "Acquire, renew or release a memory lease. Acquire returns the amount ACTUALLY " +
                    "freed (achievedAvailableGb) and how big the shortfall is (shortfallGb) — branch " +
                    "on that number, do not assume success. Only one lease can be active at a time " +
                    "(a conflict returns HTTP 409 with the current holder). If your job may outlive " +
                    "the TTL, call renew instead of letting it expire: on expiry the suspended " +
                    "processes are restored immediately, i.e. memory is taken back exactly when your " +
                    "job needs it most. Always release with the returned id when done.",
                ["inputSchema"] = Schema("""
                {
                  "type":"object",
                  "properties":{
                    "action":{"type":"string","enum":["acquire","release","renew"],"description":"acquire a new lease, renew an existing one, or release it."},
                    "targetAvailableGb":{"type":"number","description":"acquire: how much available memory the job needs, in GB."},
                    "ttlSeconds":{"type":"integer","description":"acquire: max hold in seconds (30-86400, default 3600). renew: the new TTL from now (default 3600)."},
                    "policy":{"type":"string","enum":["soft","hard"],"description":"acquire: soft (default) or hard."},
                    "requester":{"type":"string","description":"acquire: who is asking (written to the decision log)."},
                    "requesterPid":{"type":"integer","description":"acquire: PID of your own task process; it and its subtree are never suspended."},
                    "leaseId":{"type":"string","description":"release/renew: the id returned by acquire. Omit to use whatever lease is active."}
                  },
                  "required":["action"],
                  "additionalProperties":false
                }
                """)
            }
        }
    };

    // ---- tools/call ----

    private static async Task<JsonNode> ToolsCallAsync(JsonNode? p, ILeaseApi client)
    {
        var name = p?["name"]?.GetValue<string>() ?? "";
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        return name switch
        {
            "mem_status" => await StatusAsync(client),
            "mem_plan" => await PlanAsync(args, client),
            "mem_lease" => await LeaseAsync(args, client),
            _ => ToolError($"Unknown tool '{name}'. Available: mem_status, mem_plan, mem_lease.")
        };
    }

    private static async Task<JsonNode> StatusAsync(ILeaseApi client)
    {
        var r = await client.CallAsync(HttpMethod.Get, "/api/status");
        return r.Ok ? ToolText(Pretty(r.Body)) : ToolError(client.Explain(r));
    }

    private static async Task<JsonNode> PlanAsync(JsonObject args, ILeaseApi client)
    {
        var gb = Num(args, "targetAvailableGb");
        if (gb is null || gb <= 0) return ToolError("targetAvailableGb must be a number > 0");

        var body = new JsonObject
        {
            ["targetAvailableGb"] = gb,
            ["policy"] = Str(args, "policy") ?? "soft",
            ["requester"] = Str(args, "requester") ?? "mcp",
            // 默认排除 MCP 自己（它通常就是 agent 的子进程）；模型可以传自己的任务 PID 扩大保护范围
            ["requesterPid"] = Pid(args) ?? Environment.ProcessId
        };
        var r = await client.CallAsync(HttpMethod.Post, "/api/plan", body.ToJsonString());
        return r.Ok ? ToolText(Pretty(r.Body)) : ToolError(client.Explain(r));
    }

    private static async Task<JsonNode> LeaseAsync(JsonObject args, ILeaseApi client)
    {
        var action = Str(args, "action") ?? "";
        if (action == "acquire")
        {
            var gb = Num(args, "targetAvailableGb");
            if (gb is null || gb <= 0) return ToolError("acquire requires targetAvailableGb > 0");

            var body = new JsonObject
            {
                ["targetAvailableGb"] = gb,
                ["ttlSeconds"] = (int?)Num(args, "ttlSeconds") ?? 3600,
                ["policy"] = Str(args, "policy") ?? "soft",
                ["requester"] = Str(args, "requester") ?? "mcp",
                ["requesterPid"] = Pid(args) ?? Environment.ProcessId
            };
            var r = await client.CallAsync(HttpMethod.Post, "/api/lease", body.ToJsonString());
            if (!r.Ok) return ToolError(client.Explain(r));

            // 摘要 + 原始 JSON：模型先用一句话判断"够不够"，需要细节时再看 JSON
            var summary = SummarizeLease(r.Body);
            return ToolText(summary + "\n\n" + Pretty(r.Body));
        }

        if (action == "release")
        {
            var id = Str(args, "leaseId");
            if (string.IsNullOrEmpty(id))
            {
                // 便利：不带 id 时释放当前活跃租约，省掉一次 mem_status 往返
                var cur = await client.CallAsync(HttpMethod.Get, "/api/lease");
                if (cur.Status == 404) return ToolText("No active lease to release.");
                if (!cur.Ok) return ToolError(client.Explain(cur));
                id = TryReadId(cur.Body);
                if (string.IsNullOrEmpty(id)) return ToolError("Could not read the active lease id: " + cur.Body);
            }

            var r = await client.CallAsync(HttpMethod.Delete, "/api/lease/" + Uri.EscapeDataString(id));
            if (!r.Ok) return ToolError(client.Explain(r));
            return ToolText(SummarizeRelease(r.Body) + "\n\n" + Pretty(r.Body));
        }

        if (action == "renew")
        {
            var id = Str(args, "leaseId");
            if (string.IsNullOrEmpty(id))
            {
                var cur = await client.CallAsync(HttpMethod.Get, "/api/lease");
                if (cur.Status == 404) return ToolText("No active lease to renew.");
                if (!cur.Ok) return ToolError(client.Explain(cur));
                id = TryReadId(cur.Body);
                if (string.IsNullOrEmpty(id)) return ToolError("Could not read the active lease id: " + cur.Body);
            }

            var body = new JsonObject { ["ttlSeconds"] = (int?)Num(args, "ttlSeconds") ?? 3600 };
            var r = await client.CallAsync(HttpMethod.Post,
                $"/api/lease/{Uri.EscapeDataString(id)}/renew", body.ToJsonString());
            if (!r.Ok) return ToolError(client.Explain(r));   // 410 由 Explain 说清"已过期、只能重申请"
            return ToolText(SummarizeRenew(r.Body) + "\n\n" + Pretty(r.Body));
        }

        return ToolError("action must be 'acquire', 'release' or 'renew'");
    }

    // ---- 摘要（模型先读这句，再决定要不要看 JSON）----

    private static string SummarizeLease(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            double achieved = root.TryGetProperty("achievedAvailableGb", out var a) ? a.GetDouble() : 0;
            double target = root.TryGetProperty("targetAvailableGb", out var t) ? t.GetDouble() : 0;
            double shortfall = root.TryGetProperty("shortfallGb", out var s) ? s.GetDouble() : 0;
            int suspended = root.TryGetProperty("suspended", out var su) && su.ValueKind == JsonValueKind.Array
                ? su.GetArrayLength() : 0;
            return $"Lease active: achieved {achieved:F1} GB of {target:F1} GB requested " +
                   $"(shortfall {shortfall:F1} GB), {suspended} process(es) suspended. " +
                   (shortfall > 0.05
                       ? "The job may not fit — consider reducing its memory footprint, then release this lease."
                       : "Call mem_lease with action=release and this id when the job finishes.");
        }
        catch { return "Lease acquired."; }
    }

    private static string SummarizeRenew(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var expires = root.TryGetProperty("expiresUtc", out var e) ? e.GetString() : "?";
            int count = root.TryGetProperty("renewCount", out var rc) && rc.TryGetInt32(out var n) ? n : 0;
            double remaining = root.TryGetProperty("remainingSeconds", out var rs) && rs.TryGetDouble(out var s) ? s : 0;
            return $"Lease renewed (#{count}), valid for another {remaining / 60:F0} min (until {expires}).";
        }
        catch { return "Lease renewed."; }
    }

    private static string SummarizeRelease(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var st) ? st.GetString() : "?";
            int total = 0, restored = 0;
            if (root.TryGetProperty("suspended", out var su) && su.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in su.EnumerateArray())
                {
                    total++;
                    if (p.TryGetProperty("restored", out var r) && r.ValueKind == JsonValueKind.True) restored++;
                }
            }
            return $"Lease {state}. Suspended processes restored: {restored}/{total}.";
        }
        catch { return "Lease released."; }
    }

    private static string TryReadId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    // ---- JSON 小工具 ----

    private static string Pretty(string json)
    {
        try { return JsonNode.Parse(json)?.ToJsonString(PrettyJson) ?? json; }
        catch { return json; }
    }

    private static double? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static int? Pid(JsonObject o, string key = "requesterPid") =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) && i > 0 ? i : null;

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static JsonNode ToolText(string text) => new JsonObject
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = false
    };

    private static JsonNode ToolError(string message) => new JsonObject
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message } },
        ["isError"] = true
    };
}
