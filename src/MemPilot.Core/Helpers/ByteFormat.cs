namespace MemPilot.Helpers;

/// <summary>字节数的统一人类可读格式（内存/显存/回收量共用一套口径）。</summary>
public static class ByteFormat
{
    public static string Human(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }
}
