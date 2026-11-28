using System.IO;
using System.Threading;
using System.Windows;
using MemPilot.Services;

namespace MemPilot.App;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showSignal;
    private const string ShowSignalName = @"Local\MemPilot.ShowSignal";

    /// <summary>The running instance wires this to its safe WPF show path.</summary>
    public static event Action? ShowRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        // global exception safety net: log, tell the user, keep running if possible
        DispatcherUnhandledException += (s, args) =>
        {
            TryLogCrash(args.Exception);
            MessageBox.Show("未处理异常: " + args.Exception.Message, "MemPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) => TryLogCrash(args.ExceptionObject as Exception);

        // v0.23.0：MCP server 必须在单实例检查**之前**拦截。
        // 它是被 agent spawn 的独立进程、只做 HTTP 客户端；若走单实例路径，它会去唤起
        // 已有窗口然后退出，MCP 直接失效。同步阻塞到 stdin EOF——MCP 模式没有 UI、
        // 也不需要 Dispatcher，所以 Environment.Exit 是正确退出方式（不走 WPF 清理）。
        bool isMcp = false;
        foreach (var arg in e.Args)
            if (arg is "--mcp" or "/mcp") { isMcp = true; break; }
        if (isMcp)
        {
            int exitCode = Mcp.McpServer.Run();
            // 用 Shutdown 而不是 Environment.Exit：后者在 WPF OnStartup 期间会卡在
            // Dispatcher/终结器等待上（实测：进程活着但不退出）。Shutdown 让消息循环正常收尾。
            Shutdown(exitCode);
            return;
        }

        // v0.25.0：`mempilot lease run ...` 同样必须在单实例检查之前拦截。
        // 它只做 HTTP 客户端 + 子进程托管，不启动 GUI；走单实例路径会被当成"第二个实例"
        // 去唤起已有窗口然后退出。Task.Run 是为了脱离 OnStartup 的 SynchronizationContext
        // ——里面用 GetAwaiter().GetResult() 等 HTTP，同步等待会死锁。
        if (e.Args.Length >= 2 &&
            (e.Args[0] is "lease" or "/lease") &&
            string.Equals(e.Args[1], "run", StringComparison.OrdinalIgnoreCase))
        {
            int exitCode = Task.Run(() => Cli.LeaseRun.Run(e.Args[2..])).GetAwaiter().GetResult();
            Shutdown(exitCode);
            return;
        }

        // single instance: two copies would fight over standby purges and suspend registries
        _singleInstance = new Mutex(true, @"Local\MemPilot.SingleInstance", out var isNew);
        if (!isNew)
        {
            // v0.9.0 修：旧代码在这里调 `_showSignal?.Set()`，但该字段只在下面（第一实例分支）
            // 才赋值 → 恒为 null，第二实例什么也没做就退出了（用户看到"双击没反应"）。
            // 正确做法是打开已存在的那颗命名事件再 Set。
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch (Exception ex) { TryLogCrash(ex); }
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
        // v0.19.0：语言字典必须在首个窗口创建前加载（StartupUri 在 OnStartup 返回后才实例化）
        try { Localization.WpfStrings.Apply(new ConfigStore(ResolveDataDir()).Config.Language); }
        catch (Exception i18n) { MemPilot.Helpers.Diag.Record("i18n:apply", i18n); }
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
                    MessageBox.Show(UiStrings.Get("App_ResetDone"), "MemPilot", MessageBoxButton.OK, MessageBoxImage.Information);
                    Application.Current.Shutdown();
                    return;
            }
        }
        if (areas.Count > 0)
        {
            if (!cleaner.IsAdmin())
            {
                MessageBox.Show(UiStrings.Get("App_ConsoleNeedsAdmin"), "MemPilot", MessageBoxButton.OK, MessageBoxImage.Error);
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

    internal static string ResolveDataDir() => MemPilotPaths.ResolveDataDir();
}
