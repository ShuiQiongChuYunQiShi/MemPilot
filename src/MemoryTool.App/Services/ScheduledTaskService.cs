using System.Diagnostics;
using System.IO;

namespace MemoryTool.Services;

public class ScheduledTaskInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Status { get; set; } = "";
    public string LastRun { get; set; } = "";
    public string NextRun { get; set; } = "";
}

public class ScheduledTaskService
{
    public List<ScheduledTaskInfo> Enumerate()
    {
        var list = new List<ScheduledTaskInfo>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/query /fo CSV /nh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return list;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = ParseCsv(line);
                if (parts.Count >= 2)
                {
                    list.Add(new ScheduledTaskInfo
                    {
                        Name = parts.Count > 0 ? parts[0].Trim('"') : "",
                        Path = parts.Count > 1 ? parts[1].Trim('"') : "",
                        Status = parts.Count > 2 ? parts[2].Trim('"') : "",
                        LastRun = parts.Count > 3 ? parts[3].Trim('"') : "",
                        NextRun = parts.Count > 4 ? parts[4].Trim('"') : ""
                    });
                }
            }
        }
        catch { }
        return list;
    }

    public bool IsScheduledTaskProcess(string exePath, List<ScheduledTaskInfo> tasks)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        var name = Path.GetFileName(exePath).ToLowerInvariant();
        foreach (var t in tasks)
        {
            if (t.Path.ToLowerInvariant().Contains(name) || t.Name.ToLowerInvariant().Contains(name.Replace(".exe", "")))
                return true;
        }
        return false;
    }

    private static List<string> ParseCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; current.Append(c); }
            else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
