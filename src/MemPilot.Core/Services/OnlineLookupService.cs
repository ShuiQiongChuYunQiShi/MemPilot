using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MemPilot.Helpers;
using MemPilot.Models;

namespace MemPilot.Services;

public class OnlineLookupService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    public AppConfig Config { get; set; } = new();

    // per-path result cache: refreshes must not re-query the same binaries; persisted to lookups.json
    private class CachedLookup
    {
        public DateTime Time { get; set; }
        public OnlineLookupResult Result { get; set; } = new();
    }

    private static readonly Dictionary<string, CachedLookup> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();
    private const int CacheDays = 7;
    private const int CacheMaxEntries = 512;
    private static string? _cachePath;

    public static void LoadCache(string dataDir)
    {
        _cachePath = Path.Combine(dataDir, "lookups.json");
        var loaded = JsonFileStore.Load<Dictionary<string, CachedLookup>>(_cachePath);
        if (loaded == null) return;
        lock (_cacheLock)
        {
            foreach (var kv in loaded)
                if ((DateTime.UtcNow - kv.Value.Time).TotalDays < CacheDays)
                    _cache[kv.Key] = kv.Value;
        }
    }

    private static void SaveCache()
    {
        if (_cachePath == null) return;
        lock (_cacheLock)
        {
            if (_cache.Count > CacheMaxEntries)
            {
                foreach (var dead in _cache.OrderBy(kv => kv.Value.Time).Take(_cache.Count - CacheMaxEntries).Select(kv => kv.Key).ToList())
                    _cache.Remove(dead);
            }
            JsonFileStore.Save(_cachePath, _cache);
        }
    }

    public async Task<OnlineLookupResult> LookupAsync(ProcessInfo p, CancellationToken ct = default)
    {
        var res = await LookupUncachedAsync(p, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(p.ExecutablePath) && res.Status is "found" or "uncertain" or "not_found")
        {
            lock (_cacheLock)
            {
                _cache[p.ExecutablePath] = new CachedLookup { Time = DateTime.UtcNow, Result = res };
            }
            SaveCache();
        }
        return res;
    }

    private async Task<OnlineLookupResult> LookupUncachedAsync(ProcessInfo p, CancellationToken ct = default)
    {
        var res = new OnlineLookupResult();
        if (!Config.OnlineLookupEnabled)
        {
            res.Status = "disabled";
            res.Summary = "联网查询已禁用";
            return res;
        }
        if (!string.IsNullOrEmpty(p.ExecutablePath))
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(p.ExecutablePath, out var hit) &&
                    (DateTime.UtcNow - hit.Time).TotalDays < CacheDays &&
                    hit.Result.Status is not ("not_configured" or "disabled"))
                    return hit.Result;
            }
        }

        // v0.5.0：端点与密钥都没配时直接返回，别去读 exe 算 SHA256（真机配置就是这样，白算了很多次）
        if (string.IsNullOrWhiteSpace(Config.OnlineLookupEndpoint) && string.IsNullOrWhiteSpace(Config.OnlineLookupApiKey))
        {
            res.Status = "not_configured";
            res.Summary = "未配置查询端点（设置 → 联网查询）；已跳过哈希计算";
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
                // v0.3.8（棱镜 security/network）：非 https 且非本机时拒绝——否则 x-api-key 会明文过网
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
                {
                    res.Status = "error";
                    res.Summary = "查询端点必须是 https（或 localhost）；http 会让 API 密钥明文过网";
                    return res;
                }
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(Config.OnlineLookupApiKey))
                    req.Headers.TryAddWithoutValidation("x-api-key", Config.OnlineLookupApiKey);
                var resp = await _http.SendAsync(req, ct);
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
                var resp = await _http.SendAsync(req, ct);
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
