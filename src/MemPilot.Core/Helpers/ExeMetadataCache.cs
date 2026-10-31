using System.Diagnostics;

namespace MemPilot.Helpers;

/// <summary>exe 文件版本信息缓存（ProcessService 与程序清单共用）。key = path + lastWrite。</summary>
public static class ExeMetadataCache
{
    public readonly record struct Meta(string Company, string Product, string Description, string Version);

    public static readonly Meta Empty = new("", "", "", "");

    private static readonly Dictionary<string, (DateTime lastWrite, Meta meta)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public static Meta Read(string path)
    {
        if (string.IsNullOrEmpty(path)) return Empty;
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(path);
            lock (_lock)
            {
                if (_cache.TryGetValue(path, out var hit) && hit.lastWrite == lastWrite) return hit.meta;
            }
            Meta meta;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                meta = new Meta(vi.CompanyName ?? "", vi.ProductName ?? "", vi.FileDescription ?? "", vi.FileVersion ?? "");
            }
            catch { meta = Empty; }
            lock (_lock)
            {
                if (_cache.Count > 4096) _cache.Clear();
                _cache[path] = (lastWrite, meta);
            }
            return meta;
        }
        catch { return Empty; }
    }
}
