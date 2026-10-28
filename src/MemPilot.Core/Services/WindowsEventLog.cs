using System.Diagnostics;

namespace MemPilot.Services;

/// <summary>Optional audit mirror into the Windows Event Log (WMC-style transparency).
/// Source creation requires elevation; failures are swallowed — audit.json stays the source of truth.</summary>
public static class WindowsEventLog
{
    private const string Source = "MemPilot";
    public static bool Enabled { get; set; }

    public static void TryWrite(string message)
    {
        if (!Enabled) return;
        try
        {
            if (!EventLog.SourceExists(Source))
                EventLog.CreateEventSource(Source, "Application");
            EventLog.WriteEntry(Source, message, EventLogEntryType.Information);
        }
        catch { /* non-elevated sessions cannot create the source; not an error */ }
    }
}
