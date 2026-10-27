using Microsoft.Win32;
using System.IO;

namespace MemoryTool.Services;

public class StartupItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Source { get; set; } = ""; // registry/folder
}

public class StartupService
{
    public List<StartupItem> Enumerate()
    {
        var list = new List<StartupItem>();
        // Registry Run keys
        var keys = new (RegistryKey root, string path, string source)[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM Run"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM RunOnce"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM WOW64 Run"),
        };
        foreach (var (root, path, source) in keys)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var val = key.GetValue(name)?.ToString() ?? "";
                    list.Add(new StartupItem { Name = name, Path = val, Source = source });
                }
            }
            catch { }
        }
        // Startup folders
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder))
                {
                    list.Add(new StartupItem { Name = Path.GetFileNameWithoutExtension(file), Path = file, Source = "Startup Folder" });
                }
            }
            catch { }
        }
        return list;
    }

    public static bool IsStartupPath(string exePath, List<StartupItem> items)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        var name = Path.GetFileName(exePath).ToLowerInvariant();
        foreach (var it in items)
        {
            var p = it.Path.ToLowerInvariant();
            if (p.Contains(exePath.ToLowerInvariant()) || p.Contains(name)) return true;
        }
        return false;
    }
}
