using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MemPilot.Helpers;

/// <summary>Global hotkey via RegisterHotKey on a hidden message window.
/// Spec format: "CTRL+SHIFT+M" — CTRL/SHIFT/ALT/WIN + a letter, digit or F-key.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    private const uint MOD_NOREPEAT = 0x4000;

    private HwndSource? _source;
    private bool _registered;

    public event Action? Activated;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("MemPilotHotkeyWindow", 1, 1)
        {
            WindowStyle = 0,
            PositionX = -32000,
            PositionY = -32000
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == 1)
        {
            handled = true;
            Activated?.Invoke();
        }
        return IntPtr.Zero;
    }

    public bool TryRegister(string spec)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(spec)) return false;
        if (!TryParse(spec, out var mods, out var vk)) return false;
        if (_source == null) return false;
        _registered = RegisterHotKey(_source.Handle, 1, mods | MOD_NOREPEAT, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source != null)
            UnregisterHotKey(_source.Handle, 1);
        _registered = false;
    }

    private static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": mods |= MOD_CONTROL; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "ALT": mods |= MOD_ALT; break;
                case "WIN" or "WINDOWS": mods |= MOD_WIN; break;
                default:
                    if (part.Length == 1 && (char.IsAsciiLetterOrDigit(part[0])))
                        vk = part[0]; // letters and digits share their ASCII VK codes
                    else if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
                        vk = (uint)(0x70 + f - 1); // VK_F1 = 0x70
                    else
                        return false;
                    break;
            }
        }
        return vk != 0;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
