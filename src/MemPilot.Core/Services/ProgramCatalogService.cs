using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MemPilot.Helpers;
using MemPilot.Models;
using Microsoft.Win32;

namespace MemPilot.Services;

/// <summary>
/// 程序清单（v0.3.0）：把"已安装程序"建成索引，供分类器作为证据使用。
/// 三源：注册表 Uninstall（HKLM64/HKLM32/HKCU）+ 开始菜单 .lnk + 常见安装根按需扫描。
/// 约束：只提供证据、不构成处置依据；查询 O(1)（exe 精确→字典，安装目录→最长前缀）；
/// 构建幂等、带 SchemaVersion 与 24h TTL；全盘递归扫描刻意不做（IO 风暴/杀软干扰）。
/// </summary>
public class ProgramCatalogService
{
    public const int SchemaVersion = 1;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly Dictionary<string, InstalledProgram> _byExe = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string dir, InstalledProgram program)> _byDir = new();
    private readonly HashSet<string> _dirSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirDirty;

    public DateTime BuiltUtc { get; set; } = DateTime.MinValue;
    public int Count => _byExe.Count;
    public int DirectoryIndexCount => _byDir.Count;

    public IReadOnlyList<InstalledProgram> All() => _byExe.Values.ToList();

    public static ProgramCatalogService LoadOrBuild(string dataDir, Action<string>? progress = null)
    {
        var path = Path.Combine(dataDir, "programs.json");
        var cached = TryLoad(path);
        if (cached != null) return cached;
        var built = Build(progress);
        built.Save(dataDir);
        return built;
    }

    public static ProgramCatalogService? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<CatalogDto>(File.ReadAllText(path));
            if (dto == null || dto.SchemaVersion != SchemaVersion) return null;
            if (DateTime.UtcNow - dto.BuiltUtc > MaxAge) return null;
            var catalog = new ProgramCatalogService { BuiltUtc = dto.BuiltUtc };
            foreach (var p in dto.Programs) catalog.Index(p);
            catalog.SortDirectoryIndex();
            return catalog;
        }
        catch { return null; }
    }

    public static ProgramCatalogService Build(Action<string>? progress = null)
    {
        var catalog = new ProgramCatalogService();
        // v0.3.9：三个来源分别记诊断——清单悄悄变空是最难发现的失败（界面只会显示"未识别"变多）
        try { foreach (var p in FromRegistry()) catalog.Index(p); }
        catch (Exception ex) { Diag.Record("catalog:registry", ex); }
        progress?.Invoke($"程序清单：注册表 {catalog.Count} 条");
        try { foreach (var p in FromStartMenu()) catalog.Index(p); }
        catch (Exception ex) { Diag.Record("catalog:startmenu", ex); }
        progress?.Invoke($"程序清单：开始菜单累计 {catalog.Count} 条");
        try { foreach (var p in FromInstallRoots()) catalog.Index(p); }
        catch (Exception ex) { Diag.Record("catalog:roots", ex); }
        catalog.BuiltUtc = DateTime.UtcNow;
        catalog.SortDirectoryIndex();
        if (catalog.Count == 0)
            Diag.Record("catalog:empty", "三个来源均为空（扫描失败，或系统确实没有已安装程序）");
        progress?.Invoke($"程序清单完成：{catalog.Count} 条（目录索引 {catalog.DirectoryIndexCount}）");
        return catalog;
    }

    public void Index(InstalledProgram p)
    {
        if (!string.IsNullOrWhiteSpace(p.ExecutablePath) && !_byExe.ContainsKey(p.ExecutablePath))
        {
            if (string.IsNullOrWhiteSpace(p.Publisher) && File.Exists(p.ExecutablePath))
            {
                var meta = ExeMetadataCache.Read(p.ExecutablePath);
                p.Publisher = meta.Company;
                if (string.IsNullOrWhiteSpace(p.Version)) p.Version = meta.Version;
            }
            _byExe[p.ExecutablePath] = p;
        }
        if (!string.IsNullOrWhiteSpace(p.InstallLocation) && _dirSet.Add(p.InstallLocation.TrimEnd('\\')))
        {
            _byDir.Add((p.InstallLocation.TrimEnd('\\'), p));
            _dirDirty = true;
        }
    }

    private void SortDirectoryIndex()
    {
        _byDir.Sort((a, b) => b.dir.Length.CompareTo(a.dir.Length));
        _dirDirty = false;
    }

    /// <summary>exe 精确命中 → 安装目录最长前缀命中（要求目录边界为 \）。</summary>
    public InstalledProgram? FindByPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        if (_byExe.TryGetValue(exePath, out var exact)) return exact;
        if (_dirDirty) SortDirectoryIndex(); // 缓存加载/直接 Index 后保证最长前缀优先
        try
        {
            var full = Path.GetFullPath(exePath);
            foreach (var (dir, program) in _byDir)
            {
                if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                if (full.Length > dir.Length && full[dir.Length] != '\\') continue;
                return program;
            }
        }
        catch { }
        return null;
    }

    public void Save(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var path = Path.Combine(dataDir, "programs.json");
            var dto = new CatalogDto { SchemaVersion = SchemaVersion, BuiltUtc = BuiltUtc, Programs = _byExe.Values.ToList() };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    // ---- 源 1：注册表 Uninstall（32/64 双视图） ----

    private static IEnumerable<InstalledProgram> FromRegistry()
    {
        var views = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        foreach (var (hive, view) in views)
        {
            RegistryKey? baseKey = null;
            try { baseKey = RegistryKey.OpenBaseKey(hive, view); } catch { }
            if (baseKey == null) continue;
            using (baseKey)
            {
                RegistryKey? uninstall = null;
                try { uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"); } catch { }
                if (uninstall == null) continue;
                using (uninstall)
                {
                    string[] subs;
                    try { subs = uninstall.GetSubKeyNames(); } catch { continue; }
                    foreach (var sub in subs)
                    {
                        InstalledProgram? program = null;
                        try
                        {
                            using var k = uninstall.OpenSubKey(sub);
                            if (k == null) continue;
                            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var name in k.GetValueNames()) values[name] = k.GetValue(name);
                            program = ProgramFromValues(values, "registry");
                            if (program != null && string.IsNullOrWhiteSpace(program.ExecutablePath) &&
                                !string.IsNullOrWhiteSpace(program.InstallLocation))
                                program.ExecutablePath = FindPrimaryExe(program.InstallLocation, 2) ?? "";
                        }
                        catch { }
                        if (program != null) yield return program;
                    }
                }
            }
        }
    }

    /// <summary>纯函数：把 Uninstall 键值转成程序条目（可单测，不做 IO）。</summary>
    public static InstalledProgram? ProgramFromValues(IReadOnlyDictionary<string, object?> values, string source)
    {
        static string? Str(IReadOnlyDictionary<string, object?> d, string key)
            => d.TryGetValue(key, out var v) ? v?.ToString() : null;

        try
        {
            // 过滤系统组件/补丁子项
            if (values.TryGetValue("SystemComponent", out var sc) && sc != null && Convert.ToInt32(sc) == 1) return null;

            var name = Str(values, "DisplayName")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;

            var icon = Str(values, "DisplayIcon")?.Trim();
            var installLoc = Str(values, "InstallLocation")?.Trim()?.Trim('"');
            var uninstall = Str(values, "UninstallString")?.Trim();

            return new InstalledProgram
            {
                DisplayName = name,
                Publisher = Str(values, "Publisher")?.Trim() ?? "",
                Version = Str(values, "DisplayVersion")?.Trim() ?? "",
                InstallLocation = installLoc ?? "",
                IconPath = icon ?? "",
                ExecutablePath = ExtractExePath(icon) ?? ExtractQuotedPath(uninstall) ?? "",
                Source = source
            };
        }
        catch { return null; }
    }

    private static string? ExtractExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        var comma = s.LastIndexOf(',');
        if (comma > 2 && int.TryParse(s[(comma + 1)..], out _)) s = s[..comma];
        s = s.Trim().Trim('"');
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    private static string? ExtractQuotedPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 1) s = s[1..end];
        }
        else
        {
            var exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0) s = s[..(exeIdx + 4)];
            else return null;
        }
        s = s.Trim();
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    // ---- 源 2：开始菜单 .lnk（Shell Link COM） ----

    private static IEnumerable<InstalledProgram> FromStartMenu()
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        };
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var lnk in files)
            {
                InstalledProgram? p = null;
                try
                {
                    var target = TryReadShortcutTarget(lnk);
                    if (string.IsNullOrWhiteSpace(target) ||
                        !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(target)) continue;
                    p = new InstalledProgram
                    {
                        DisplayName = Path.GetFileNameWithoutExtension(lnk),
                        InstallLocation = Path.GetDirectoryName(target) ?? "",
                        ExecutablePath = target,
                        Source = "startmenu"
                    };
                }
                catch { }
                if (p != null) yield return p;
            }
        }
    }

    /// <summary>解析 .lnk 目标（IShellLinkW + IPersistFile）。COM 不可用时返回 null，不抛错。</summary>
    public static string? TryReadShortcutTarget(string lnkPath)
    {
        object? obj = null;
        bool comInitialized = false;
        try
        {
            var hrInit = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
            comInitialized = hrInit >= 0;
            var clsid = NativeMethods.CLSID_ShellLink;
            var iid = NativeMethods.IID_IShellLinkW;
            if (NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, ref iid, out obj) != 0 || obj == null)
                return null;
            if (obj is not NativeMethods.IPersistFile pf || obj is not NativeMethods.IShellLinkW link) return null;
            pf.Load(lnkPath, 0 /* STGM_READ */);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
        finally
        {
            if (obj != null) { try { Marshal.ReleaseComObject(obj); } catch { } }
            if (comInitialized) { try { NativeMethods.CoUninitialize(); } catch { } }
        }
    }

    // ---- 源 3：常见安装根（固定深度、可重复、不递归全盘） ----

    private static IEnumerable<InstalledProgram> FromInstallRoots()
    {
        var roots = new List<string>();
        void Add(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) roots.Add(root);
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(common)) Add(Path.Combine(common, "Programs"));

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                if (drive.RootDirectory.FullName.StartsWith(systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
                Add(Path.Combine(drive.RootDirectory.FullName, "Program Files"));
                Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)"));
            }
            catch { }
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(root); } catch { continue; }
            foreach (var sub in subs)
            {
                InstalledProgram? p = null;
                try
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var exe = FindPrimaryExe(sub, depth: 2);
                    if (exe != null)
                    {
                        p = new InstalledProgram
                        {
                            DisplayName = info.Name,
                            InstallLocation = sub,
                            ExecutablePath = exe,
                            Source = "scan"
                        };
                    }
                }
                catch { }
                if (p != null) yield return p;
            }
        }
    }

    /// <summary>目录内优先找与目录同名的 exe，其次任意 exe；limited depth 内递归。</summary>
    private static string? FindPrimaryExe(string dir, int depth)
    {
        try
        {
            var dirName = Path.GetFileName(dir.TrimEnd('\\'));
            var top = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
            var same = top.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(dirName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(same)) return same;
            var first = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrEmpty(first)) return first;
            if (depth <= 1) return null;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(sub);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var exe = FindPrimaryExe(sub, depth - 1);
                if (!string.IsNullOrEmpty(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    private class CatalogDto
    {
        public int SchemaVersion { get; set; }
        public DateTime BuiltUtc { get; set; }
        public List<InstalledProgram> Programs { get; set; } = new();
    }
}
