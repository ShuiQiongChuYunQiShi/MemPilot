using System.Configuration;
using System.Data;
using System.Windows;
using Application = System.Windows.Application;
using MemoryTool.Services;

namespace MemoryTool.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Console mode: /StandbyList /WorkingSet etc.
        var args = e.Args;
        if (args.Length > 0 && args[0].StartsWith("/"))
        {
            var cleaner = new NativeMemoryCleanerService();
            var areas = new List<MemoryArea>();
            foreach (var arg in args)
            {
                switch (arg.ToLowerInvariant())
                {
                    case "/combinedpagelist": areas.Add(MemoryArea.CombinedPageList); break;
                    case "/modifiedfilecache": areas.Add(MemoryArea.ModifiedFileCache); break;
                    case "/modifiedpagelist": areas.Add(MemoryArea.ModifiedPageList); break;
                    case "/registrycache": areas.Add(MemoryArea.RegistryCache); break;
                    case "/standbylist": areas.Add(MemoryArea.StandbyList); break;
                    case "/standbylistlowpriority": areas.Add(MemoryArea.StandbyListLowPriority); break;
                    case "/systemfilecache": areas.Add(MemoryArea.SystemFileCache); break;
                    case "/workingset": areas.Add(MemoryArea.WorkingSet); break;
                    case "/reset":
                        // Reset to factory defaults (placeholder)
                        System.Windows.MessageBox.Show("Reset to factory defaults not yet implemented.", "Info");
                        Shutdown();
                        return;
                }
            }
            if (areas.Count > 0)
            {
                if (!cleaner.IsAdmin())
                {
                    System.Windows.MessageBox.Show("Administrator privileges required for console mode.", "Error");
                    Shutdown();
                    return;
                }
                var results = cleaner.CleanAreas(areas);
                foreach (var kv in results)
                    Console.WriteLine($"{kv.Key}: {(kv.Value.success ? "OK" : "FAIL")} {kv.Value.message}");
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
