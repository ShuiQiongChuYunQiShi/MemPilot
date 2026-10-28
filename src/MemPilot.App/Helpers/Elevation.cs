using System.Diagnostics;
using System.Security.Principal;

namespace MemPilot.Helpers;

/// <summary>On-demand elevation: the app runs as the invoking user; system-level memory
/// cleaning offers a restart-as-admin that carries the chosen areas as console args
/// (the console mode executes them and exits).</summary>
public static class Elevation
{
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static bool RestartAsAdmin(string? arguments = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? "",
                UseShellExecute = true,
                Verb = "runas" // triggers UAC
            });
            return true;
        }
        catch { return false; } // user declined UAC
    }
}
