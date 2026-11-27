using System.Net.Http;

namespace MemPilot.Services;

/// <summary>
/// 租约 API 客户端的抽象（v0.25.0）。
///
/// 存在的唯一理由：让 MCP 协议层能被单测（Fake 返回预设响应，不用起真 HTTP）。
/// 生产实现是 <see cref="LeaseHttp"/>；它同时服务 `mempilot lease run` 与 MCP server，
/// 这样端口/token 发现逻辑只有一份。
/// </summary>
public interface ILeaseApi
{
    bool HasToken { get; }
    Task<LeaseHttp.Result> CallAsync(HttpMethod method, string path, string? json = null,
        CancellationToken ct = default);
    string Explain(LeaseHttp.Result result);
}
