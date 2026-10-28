using MemPilot.Models;

namespace MemPilot.Services;

public class ModeManager
{
    public AppConfig Config { get; }
    public ModeManager(AppConfig config) { Config = config; }
    public AppMode Current => Config.CurrentMode;
    public void SetMode(AppMode mode) { Config.CurrentMode = mode; }
    public ModeProfile Profile => Config.ModeProfiles.TryGetValue(Config.CurrentMode, out var p) ? p : new ModeProfile { Mode = Config.CurrentMode, Name = Config.CurrentMode.ToString() };

    public bool ShouldKeep(ProcessInfo p)
    {
        var prof = Profile;
        var name = p.Name.ToLowerInvariant();
        var path = p.ExecutablePath.ToLowerInvariant();
        foreach (var pat in prof.KeepPatterns)
        {
            if (name.Contains(pat.ToLowerInvariant()) || path.Contains(pat.ToLowerInvariant())) return true;
        }
        // category priority high => keep
        if (prof.Priority.TryGetValue(p.Category, out var pr) && pr >= 8) return true;
        return false;
    }
    public bool ShouldClean(ProcessInfo p)
    {
        var prof = Profile;
        var name = p.Name.ToLowerInvariant();
        var path = p.ExecutablePath.ToLowerInvariant();
        foreach (var pat in prof.CleanPatterns)
        {
            if (name.Contains(pat.ToLowerInvariant()) || path.Contains(pat.ToLowerInvariant())) return true;
        }
        if (prof.Priority.TryGetValue(p.Category, out var pr) && pr <= 2) return true;
        return false;
    }
}
