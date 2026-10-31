using System.Runtime.InteropServices;

namespace MemPilot.Services;

/// <summary>输入活跃度信号（v0.2.2）：只读"距上次输入的秒数"，不采集任何按键内容。
/// 游戏=持续输入、看视频≈零输入、工作=键入突发——这是区分"在玩/在看/在干活"的关键信号。</summary>
public static class InputActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>距上次键盘/鼠标输入的秒数（含 TickCount 回绕处理）。失败时返回 0（视为刚有输入，保守）。</summary>
    public static double IdleSeconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;
            uint now = unchecked((uint)Environment.TickCount);
            uint deltaMs = unchecked(now - lii.dwTime); // 回绕安全（49.7 天）
            return deltaMs / 1000.0;
        }
        catch { return 0; }
    }
}
