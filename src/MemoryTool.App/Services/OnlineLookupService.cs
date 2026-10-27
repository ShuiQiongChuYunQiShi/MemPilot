using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MemoryTool.Models;

namespace MemoryTool.Services;

public class OnlineLookupService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    public AppConfig Config { get; set; } = new();

    public async Task<OnlineLookupResult> LookupAsync(ProcessInfo p)
    {
        var res = new OnlineLookupResult();
        if (!Config.OnlineLookupEnabled)
        {
            res.Status = "disabled";
            res.Summary = "联网查询已禁用";
            return res;
        }

        string hash = "";
        try
        {
            if (!string.IsNullOrEmpty(p.ExecutablePath) && File.Exists(p.ExecutablePath))
            {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(p.ExecutablePath);
                hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(Config.OnlineLookupEndpoint))
        {
            try
            {
                var url = Config.OnlineLookupEndpoint
                    .Replace("{name}", Uri.EscapeDataString(p.Name))
                    .Replace("{hash}", Uri.EscapeDataString(hash))
                    .Replace("{publisher}", Uri.EscapeDataString(p.Company))
                    .Replace("{product}", Uri.EscapeDataString(p.ProductName));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(Config.OnlineLookupApiKey))
                    req.Headers.TryAddWithoutValidation("x-api-key", Config.OnlineLookupApiKey);
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    res.Status = "error";
                    res.Summary = $"HTTP {(int)resp.StatusCode}";
                    return res;
                }
                var body = await resp.Content.ReadAsStringAsync();
                res.Evidence["hash"] = hash;
                res.Evidence["name"] = p.Name;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("category", out var catEl))
                    {
                        var catStr = catEl.GetString() ?? "";
                        if (Enum.TryParse<ProcessCategory>(catStr, true, out var cat))
                        {
                            res.SuggestedCategory = cat;
                            res.Status = "found";
                        }
                    }
                    if (root.TryGetProperty("summary", out var sumEl)) res.Summary = sumEl.GetString() ?? "";
                    if (root.TryGetProperty("source", out var srcEl)) res.SourceUrl = srcEl.GetString() ?? "";
                    if (res.Status == "unknown") res.Status = "uncertain";
                }
                catch
                {
                    res.Status = "uncertain";
                    res.Summary = body.Length > 200 ? body[..200] : body;
                }
                return res;
            }
            catch (HttpRequestException)
            {
                res.Status = "offline";
                res.Summary = "网络不可用";
                return res;
            }
            catch (Exception ex)
            {
                res.Status = "error";
                res.Summary = ex.Message;
                return res;
            }
        }

        if (!string.IsNullOrWhiteSpace(Config.OnlineLookupApiKey) && !string.IsNullOrEmpty(hash))
        {
            try
            {
                var url = $"https://www.virustotal.com/api/v3/files/{hash}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("x-apikey", Config.OnlineLookupApiKey);
                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    res.Status = "uncertain";
                    res.Summary = "VirusTotal 已返回数据，但无本地分类映射，需人工复核";
                    res.Evidence["hash"] = hash;
                    res.SourceUrl = $"https://www.virustotal.com/gui/file/{hash}";
                    return res;
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    res.Status = "not_found";
                    res.Summary = "VirusTotal 未收录该文件";
                    return res;
                }
            }
            catch (HttpRequestException)
            {
                res.Status = "offline"; res.Summary = "网络不可用"; return res;
            }
            catch (Exception ex) { res.Status = "error"; res.Summary = ex.Message; return res; }
        }

        res.Status = "not_configured";
        res.Summary = "未配置联网查询端点，仅使用本地证据";
        res.Evidence["hash"] = hash;
        return res;
    }
}
