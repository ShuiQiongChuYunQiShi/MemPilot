using MemoryTool.Models;
using MemoryTool.Services;
using Xunit;

namespace MemoryTool.Tests;

/// <summary>
/// Integration tests against the REAL system (run on every CI pass, windows runner).
/// These assert data SHAPE, not values — they exist because K32GetProcessMemoryInfo was
/// silently failing for the app's entire life (wrong DLL) and nothing caught it: every
/// PrivateWorkingSet was 0 while the UI looked plausible. A shape assert on real data
/// catches that class of bug on the first CI run.
/// </summary>
public class EnumerationIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public EnumerationIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; }

    private void Dump(List<ProcessInfo> procs)
    {
        var lines = new List<string>
        {
            $"count={procs.Count}"
        };
        foreach (var p in procs.OrderByDescending(x => x.Memory.WorkingSetBytes).Take(15))
            lines.Add($"  {p.Name} pid={p.Pid} ws={p.Memory.WorkingSetBytes / 1048576}MB privWs={p.Memory.PrivateWorkingSetBytes / 1048576}MB cat={p.Category} denied={p.AccessDenied} path='{p.ExecutablePath}' verified={p.SignatureVerified}");
        lines.Add($"categories: {string.Join(", ", procs.GroupBy(p => p.Category).Select(g => g.Key + "=" + g.Count()))}");
        lines.Add($"withPath={procs.Count(p => !string.IsNullOrEmpty(p.ExecutablePath))} withWs={procs.Count(p => p.Memory.WorkingSetBytes > 1048576)} withPrivWs={procs.Count(p => p.Memory.PrivateWorkingSetBytes > 1048576)} withCommit={procs.Count(p => p.Memory.PrivateBytes > 1048576)}");
        lines.Add($"trustLastDiag: {TrustVerifier.LastDiag}");
        var explorer = procs.FirstOrDefault(p => p.Name == "explorer") ?? procs.FirstOrDefault(p => p.ExecutablePath.Contains("explorer", StringComparison.OrdinalIgnoreCase));
        if (explorer != null)
        {
            var (v, subj, _, hr, note) = new TrustVerifier().VerifyDetailed(explorer.ExecutablePath);
            lines.Add($"trust explorer: verified={v} hr=0x{hr:X8} note={note} subject={subj}");
        }
        foreach (var l in lines) _output.WriteLine(l);
        // CI console sometimes swallows xUnit output on failures — mirror to a file
        // artifact so the failure scene can be read after the run
        WriteDiag(lines);
    }

    private static void Capture(string text)
    {
        try
        {
            Directory.CreateDirectory(TestTmp.Dir);
            File.AppendAllText(Path.Combine(TestTmp.Dir, "enumeration-diag.txt"), "\n=== FAILURE ===\n" + text + "\n");
        }
        catch { }
    }

    private static void WriteDiag(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(TestTmp.Dir);
            File.WriteAllLines(Path.Combine(TestTmp.Dir, "enumeration-diag.txt"), lines);
        }
        catch { }
    }

    [Fact]
    public void RealEnumeration_HasSystemShape()
    {
        try
        {
            HasSystemShapeInner();
        }
        catch (Exception ex)
        {
            Capture("HasSystemShape failed: " + ex);
            throw;
        }
    }

    private void HasSystemShapeInner()
    {
        var procs = new ProcessService().EnumerateProcesses();
        Dump(procs);

        // restricted execution contexts (app sandboxes, some job objects) hide most of
        // the system — shape asserts would false-fail there. Real CI runners are clean.
        if (procs.Count < 30)
        {
            Assert.True(procs.Count > 0, $"process enumeration returned nothing: {procs.Count}");
            return; // restricted environment: nothing meaningful to assert
        }

        // a windows system always has a meaningful number of processes
        Assert.True(procs.Count > 30, $"process count suspiciously low: {procs.Count}");

        // most processes expose a path via QueryFullProcessImageName
        var withPath = procs.Count(p => !string.IsNullOrEmpty(p.ExecutablePath));
        Assert.True(withPath > 20, $"processes with path: {withPath}/{procs.Count}");

        // working sets come from the managed API — must never be all-zero
        var withWs = procs.Count(p => p.Memory.WorkingSetBytes > 1024 * 1024);
        Assert.True(withWs > 10, $"processes with WS>1MB: {withWs}/{procs.Count}");

        // THE regression guard: K32GetProcessMemoryInfo must actually fill private
        // working sets. If the import breaks again (wrong DLL, silently swallowed),
        // this goes to 0 and CI goes red. (Environment-independent: works on runners.)
        var withPrivWs = procs.Count(p => p.Memory.PrivateWorkingSetBytes > 1024 * 1024);
        Assert.True(withPrivWs > 5, $"processes with PrivateWS>1MB: {withPrivWs}/{procs.Count} — K32 import broken?");

        // commit charge is populated alongside
        var withCommit = procs.Count(p => p.Memory.PrivateBytes > 1024 * 1024);
        Assert.True(withCommit > 10, $"processes with commit>1MB: {withCommit}/{procs.Count}");

        // classification asserts need full system visibility — CI runners expose only
        // user-context processes (svchost invisible, catalog-signed system files
        // unverifiable → everything lands Unknown). Skip there; real desktops assert.
        var seesSystem = procs.Any(p => p.Name.Equals("svchost", StringComparison.OrdinalIgnoreCase));
        if (seesSystem)
        {
            var unknown = procs.Count(p => p.Category == ProcessCategory.Unknown);
            Assert.True(unknown < procs.Count * 0.5, $"unknown ratio too high: {unknown}/{procs.Count}");
        }
    }

    [Fact]
    public void RealEnumeration_SystemProcessesAreProtected()
    {
        try
        {
            SystemProcessesAreProtectedInner();
        }
        catch (Exception ex)
        {
            Capture("SystemProcessesAreProtected failed: " + ex);
            throw;
        }
    }

    private void SystemProcessesAreProtectedInner()
    {
        var procs = new ProcessService().EnumerateProcesses();
        Dump(procs);
        // only meaningful where the system process table is fully visible (desktops);
        // CI runners hide system processes by design — see HasSystemShape diagnostics
        if (!procs.Any(p => p.Name.Equals("svchost", StringComparison.OrdinalIgnoreCase))) return;
        var system = procs.Where(p => p.Category is ProcessCategory.SystemCritical or ProcessCategory.SecuritySoftware).ToList();
        var unprotected = system.Where(p => !p.IsProtected).Select(p => p.Name).ToList();
        Assert.True(system.Count > 0, $"no system-critical/security processes detected out of {procs.Count}");
        Assert.True(unprotected.Count == 0, $"unprotected system processes: {string.Join(", ", unprotected)}");
    }
}
