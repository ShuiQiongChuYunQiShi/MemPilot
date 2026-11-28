using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using MemPilot.Helpers;
using MemPilot.Services;

namespace MemPilot.App.Cli;

/// <summary>
/// `mempilot lease run --gb 16 [--ttl 3600] [--policy soft|hard] [--wait 60] [--dry-run] -- python gen.py`
///
/// 为什么需要这个命令：租约的 MCP 形态只覆盖"有 MCP 客户端的 agent"，而现实里 AI 任务是
/// agent 直接跑一条命令行脚本——让 agent 自己管"申请-执行-归还"很容易漏 release。
/// 一条命令把任务包住，归还由进程边界保证（finally），agent 只需在命令前面加几个字。
///
/// requesterPid 传**包装进程自己**的 PID：子进程是它的孩子，服务端的子树遍历会自动覆盖，
/// 所以租约永远不会把任务自己的进程挂起——不需要先起子进程再回头改租约。
///
/// 退出码：子进程的退出码原样透传；2 = 参数错误；3 = MemPilot 未运行/租约 API 未开；
/// 4 = 租约被占用且 --wait 用尽；130 = Ctrl+C。
/// </summary>
internal static class LeaseRun
{
    private const int ExitUsage = 2;
    private const int ExitUnavailable = 3;
    private const int ExitConflict = 4;
    private const int ExitInterrupted = 130;

    public static int Run(string[] args)
    {
        var opt = Options.Parse(args, out var parseError);
        if (opt == null)
        {
            Console.Error.WriteLine("[mempilot] " + parseError);
            PrintUsage();
            return ExitUsage;
        }
        if (opt.Gb <= 0)
        {
            Console.Error.WriteLine("[mempilot] --gb must be > 0");
            PrintUsage();
            return ExitUsage;
        }
        if (!opt.DryRun && opt.Command.Count == 0)
        {
            Console.Error.WriteLine("[mempilot] no command given; put it after `--` (e.g. `-- python gen.py`)");
            PrintUsage();
            return ExitUsage;
        }

        var http = new LeaseHttp(port: opt.Port);
        if (!http.HasToken)
        {
            Console.Error.WriteLine("[mempilot] no lease token found. Start MemPilot once and enable " +
                                    "Settings -> 租约 API, then retry.");
            return ExitUnavailable;
        }

        var status = http.CallAsync(HttpMethod.Get, "/api/status").GetAwaiter().GetResult();
        if (!status.Ok)
        {
            Console.Error.WriteLine("[mempilot] " + http.Explain(status));
            return ExitUnavailable;
        }

        var body = JsonSerializer.Serialize(new
        {
            targetAvailableGb = opt.Gb,
            ttlSeconds = opt.Ttl,
            policy = opt.Policy,
            requester = opt.Requester,
            requesterPid = Environment.ProcessId
        });

        if (opt.DryRun)
        {
            var plan = http.CallAsync(HttpMethod.Post, "/api/plan", body).GetAwaiter().GetResult();
            if (!plan.Ok)
            {
                Console.Error.WriteLine("[mempilot] " + http.Explain(plan));
                return ExitUnavailable;
            }
            Console.WriteLine(Pretty(plan.Body));
            return 0;
        }

        var (lease, message, conflict) = AcquireWithWait(http, body, opt.WaitSeconds);
        if (lease is null)
        {
            Console.Error.WriteLine("[mempilot] " + message);
            return conflict ? ExitConflict : ExitUnavailable;
        }

        string? id = lease.Value.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        double achieved = ReadDouble(lease.Value, "achievedAvailableGb");
        double shortfall = ReadDouble(lease.Value, "shortfallGb");
        int suspended = ReadArrayLength(lease.Value, "suspended");

        Console.WriteLine($"[mempilot] lease {id}: achieved {achieved:F1}/{opt.Gb:F1} GB" +
                          (shortfall > 0.05 ? $", shortfall {shortfall:F1} GB" : "") +
                          (suspended > 0 ? $", {suspended} suspended" : ""));
        if (shortfall > 0.05)
            Console.Error.WriteLine("[mempilot] warning: memory is short — the job may still struggle; " +
                                    "consider reducing its footprint");

        try
        {
            return RunChild(opt);
        }
        finally
        {
            if (string.IsNullOrEmpty(id))
            {
                Console.Error.WriteLine("[mempilot] warning: lease id was missing; nothing to release");
            }
            else
            {
                var release = http.CallAsync(HttpMethod.Delete, "/api/lease/" + id).GetAwaiter().GetResult();
                if (release.Ok) Console.WriteLine($"[mempilot] released: {RestoredSummary(release.Body)}");
                else Console.Error.WriteLine("[mempilot] release failed: " + http.Explain(release));
            }
        }
    }

    // ---- 子进程托管 ----

    private static int RunChild(Options opt)
    {
        var (fileName, prefixArgs) = ResolveExecutable(opt.Command[0]);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false        // 需要进程句柄做 Job 绑定
            // 不重定向 stdout/stderr：子进程直接继承控制台，行为与直接调用一致
        };
        foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
        for (int i = 1; i < opt.Command.Count; i++) psi.ArgumentList.Add(opt.Command[i]);

        using var job = new JobObject();
        if (!job.IsValid)
            Console.Error.WriteLine("[mempilot] warning: could not create a job object — the child will " +
                                    "keep running if this wrapper is killed");

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mempilot] failed to start '{opt.Command[0]}': {ex.Message}");
            return ExitUnavailable;
        }

        using (proc)
        {
            if (job.IsValid && !job.Assign(proc))
                Console.Error.WriteLine("[mempilot] warning: could not bind the child to a job object — " +
                                        "it will keep running if this wrapper is killed");

            var interrupted = false;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;           // 不让进程直接退出，先收尾（还租约）
                interrupted = true;
            };

            while (!proc.WaitForExit(500))
            {
                if (!interrupted) continue;
                Console.Error.WriteLine("[mempilot] interrupted, terminating the child…");
                try { proc.Kill(entireProcessTree: true); } catch { }
                break;
            }
            proc.WaitForExit();

            if (interrupted) return ExitInterrupted;
            Console.WriteLine($"[mempilot] child exited with code {proc.ExitCode}");
            return proc.ExitCode;
        }
    }

    /// <summary>.bat/.cmd 需要 cmd.exe /c 包一层，否则 UseShellExecute=false 起不来。</summary>
    private static (string fileName, string[] prefix) ResolveExecutable(string command)
    {
        var ext = Path.GetExtension(command);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            return ("cmd.exe", new[] { "/c", command });
        return (command, Array.Empty<string>());
    }

    // ---- 申请（含 --wait 退避）----

    private static (JsonElement? lease, string message, bool conflict) AcquireWithWait(
        LeaseHttp http, string body, int waitSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            var r = http.CallAsync(HttpMethod.Post, "/api/lease", body).GetAwaiter().GetResult();
            if (r.Ok)
            {
                try
                {
                    using var doc = JsonDocument.Parse(r.Body);
                    return (doc.RootElement.Clone(), "", false);
                }
                catch (Exception ex)
                {
                    return (null, "lease API returned malformed JSON: " + ex.Message, false);
                }
            }
            if (r.Status != 409) return (null, http.Explain(r), false);

            if (DateTime.UtcNow >= deadline)
            {
                var holder = DescribeHolder(r.Body);
                return (null, $"a lease is already active{(holder is null ? "" : $" ({holder})")}. " +
                              "Wait with --wait, or release it from the other job.", true);
            }

            var retry = ReadRetryAfter(r.Body);
            var remaining = Math.Max(1, (deadline - DateTime.UtcNow).TotalSeconds);
            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(retry, remaining)));
        }
    }

    private static string? DescribeHolder(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("holder", out var h) || h.ValueKind != JsonValueKind.Object)
                return null;
            var who = h.TryGetProperty("requester", out var rq) ? rq.GetString() : "?";
            double rem = ReadDouble(h, "remainingSeconds");
            return $"held by \"{who}\", {rem:F0}s left";
        }
        catch { return null; }
    }

    private static double ReadRetryAfter(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("retryAfterSeconds", out var r) && r.TryGetDouble(out var d))
                return Math.Clamp(d, 1, 30);
        }
        catch { }
        return 5;
    }

    // ---- 输出 ----

    private static string RestoredSummary(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            int total = 0, restored = 0;
            if (doc.RootElement.TryGetProperty("suspended", out var sus) && sus.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in sus.EnumerateArray())
                {
                    total++;
                    if (p.TryGetProperty("restored", out var r) && r.ValueKind == JsonValueKind.True) restored++;
                }
            }
            return total == 0 ? "no suspended processes to restore" : $"restored {restored}/{total}";
        }
        catch { return "released"; }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }
        catch { return json; }
    }

    private static double ReadDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;

    private static int ReadArrayLength(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        usage: mempilot lease run --gb <GB> [options] -- <command> [args...]

          --gb <GB>         how much available memory the job needs (required)
          --ttl <seconds>   lease TTL, default 3600
          --policy <p>      soft (default) or hard (may suspend idle processes)
          --wait <seconds>  wait up to N seconds if another lease is active (default 0)
          --requester <n>   who is asking (written to the decision log), default "cli"
          --port <port>     override the lease API port
          --dry-run         only run plan, do not acquire or start anything

        examples:
          mempilot lease run --gb 16 -- python gen.py --steps 40
          mempilot lease run --gb 24 --policy hard -- python train.py
          mempilot lease run --gb 16 --wait 120 -- .\build.cmd
        """);

    // ---- 参数 ----

    internal sealed class Options
    {
        public double Gb;
        public int Ttl = 3600;
        public string Policy = "soft";
        public int WaitSeconds;
        public bool DryRun;
        public string Requester = "cli";
        public int? Port;
        public List<string> Command = new();

        public static Options? Parse(string[] args, out string? error)
        {
            error = null;
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--")
                {
                    o.Command = args.Skip(i + 1).ToList();
                    break;
                }
                switch (a.ToLowerInvariant())
                {
                    case "--gb":
                        if (!TryNext(args, ref i, out var gb) || !double.TryParse(gb, out o.Gb))
                        { error = "--gb needs a number"; return null; }
                        break;
                    case "--ttl":
                        if (!TryNext(args, ref i, out var ttl) || !int.TryParse(ttl, out o.Ttl))
                        { error = "--ttl needs an integer"; return null; }
                        break;
                    case "--wait":
                        if (!TryNext(args, ref i, out var wait) || !int.TryParse(wait, out o.WaitSeconds))
                        { error = "--wait needs an integer"; return null; }
                        break;
                    case "--port":
                        if (!TryNext(args, ref i, out var port) || !int.TryParse(port, out var p))
                        { error = "--port needs an integer"; return null; }
                        o.Port = p;
                        break;
                    case "--policy":
                        if (!TryNext(args, ref i, out var pol)) { error = "--policy needs a value"; return null; }
                        o.Policy = pol.ToLowerInvariant() == "hard" ? "hard" : "soft";
                        break;
                    case "--requester":
                        if (!TryNext(args, ref i, out var req)) { error = "--requester needs a value"; return null; }
                        o.Requester = req;
                        break;
                    case "--dry-run":
                        o.DryRun = true;
                        break;
                    default:
                        error = $"unknown option: {a}";
                        return null;
                }
            }
            o.WaitSeconds = Math.Clamp(o.WaitSeconds, 0, 600);
            o.Ttl = Math.Clamp(o.Ttl, 30, 86400);
            return o;
        }

        private static bool TryNext(string[] args, ref int i, out string value)
        {
            if (i + 1 >= args.Length) { value = ""; return false; }
            value = args[++i];
            return true;
        }
    }
}
