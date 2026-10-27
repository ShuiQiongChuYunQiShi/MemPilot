using System.IO;
using System.Text.RegularExpressions;

namespace MemoryTool.Services;

/// <summary>
/// Parses Steam's libraryfolders.vdf so processes running from a Steam library are
/// classified as Game by install location instead of path-name guessing.
/// Roots are cached for the process lifetime (library layout rarely changes).
/// </summary>
public static class GameLibraryService
{
    private static readonly Lazy<List<string>> _roots = new(LoadRoots);

    public static bool IsUnderGameLibrary(string exePath)
    {
        var roots = _roots.Value;
        if (roots.Count == 0 || string.IsNullOrEmpty(exePath)) return false;
        var path = exePath.Replace('/', '\\');
        foreach (var root in roots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Exposed for tests: extracts library roots from vdf text.</summary>
    public static List<string> ParseVdfRoots(string vdfText)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(vdfText, "\"path\"\\s*\"([^\"]+)\""))
        {
            var p = m.Groups[1].Value.Replace("\\\\", "\\").TrimEnd('\\');
            var entry = p + "\\";  // dedupe on the stored (trailing-slash) form, not the trimmed one
            if (p.Length > 0 && !list.Contains(entry, StringComparer.OrdinalIgnoreCase))
                list.Add(entry);
        }
        return list;
    }

    private static List<string> LoadRoots()
    {
        var roots = new List<string>();
        try
        {
            var steamRoots = new List<string>();
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86)) steamRoots.Add(Path.Combine(pf86, "Steam"));
            try
            {
                var reg = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(reg)) steamRoots.Add(reg.Replace('/', '\\'));
            }
            catch { }

            foreach (var steam in steamRoots)
            {
                var vdf = Path.Combine(steam, "config", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (var root in ParseVdfRoots(File.ReadAllText(vdf)))
                {
                    if (Directory.Exists(root.TrimEnd('\\')) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                        roots.Add(root);
                }
            }
        }
        catch { }
        return roots;
    }
}
