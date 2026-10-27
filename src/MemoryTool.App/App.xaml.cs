using System.IO;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MemoryTool.Services;

namespace MemoryTool.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showSignal;
    private const string ShowSignalName = @"Local\MemoryTool.ShowSignal";

    /// <summary>The running instance wires this to its safe WPF show path.</summary>
    public static event Action? ShowRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        // global exception safety net: log, tell the user, keep running if possible
        DispatcherUnhandledException += (s, args) =>
        {
            TryLogCrash(args.Exception);
            MessageBox.Show("未处理异常: " + args.Exception.Message, "MemoryTool", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) => TryLogCrash(args.ExceptionObject as Exception);

        // single instance: two copies would fight over standby purges and suspend registries
        _singleInstance = new Mutex(true, @"Local\MemoryTool.SingleInstance", out var isNew);
        if (!isNew)
        {
            // quiet: signal the running instance to show itself on its own dispatcher.
            // Never touch the other process's window cross-process: SW_RESTORE on a
            // WPF window that WPF believes is hidden renders a frozen black frame.
            _showSignal?.Set();
            Shutdown();
            return;
        }

        // first instance: listen for "show yourself" signals from future launches
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (s, timedOut) => Dispatcher.InvokeAsync(() => ShowRequested?.Invoke()),
            null, -1, executeOnlyOnce: false);

        var args2 = e.Args;
        if (args2.Length > 0 && args2[0].StartsWith("/"))
        {
            RunConsoleMode(args2);
            return;
        }
        base.OnStartup(e);
    }

    private static void RunConsoleMode(string[] args)
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
                    // wipe persisted state: config, feedback, audit
                    var dir = ResolveDataDir();
                    foreach (var f in new[] { "config.json", "feedback.json", "audit.json" })
                    {
                        try { File.Delete(Path.Combine(dir, f)); } catch { }
                    }
                    MessageBox.Show("已重置为出厂配置（config/feedback/audit 已删除）。", "MemoryTool", MessageBoxButton.OK, MessageBoxImage.Information);
                    Application.Current.Shutdown();
                    return;
            }
        }
        if (areas.Count > 0)
        {
            if (!cleaner.IsAdmin())
            {
                MessageBox.Show("控制台清理模式需要管理员权限。", "MemoryTool", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }
            var results = cleaner.CleanAreas(areas);
            foreach (var kv in results)
                Console.WriteLine($"{kv.Key}: {(kv.Value.success ? "OK" : "FAIL")} {kv.Value.message}");
        }
        Application.Current.Shutdown();
    }

    private static void TryLogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            File.AppendAllText(
                Path.Combine(ResolveDataDir(), "crash.log"),
                $"{DateTime.UtcNow:O}  {ex}\n\n");
        }
        catch { }
    }

    internal static string ResolveDataDir()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoryTool"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryTool"),
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Path.GetTempPath(), "MemoryTool")
        };
        foreach (var c in candidates)
        {
            try
            {
                Directory.CreateDirectory(c);
                var test = Path.Combine(c, ".writetest");
                File.WriteAllText(test, "x");
                File.Delete(test);
                return c;
            }
            catch { }
        }
        return candidates[candidates.Length - 1];
    }
}
