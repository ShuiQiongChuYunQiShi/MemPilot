using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MemoryTool.Helpers;

/// <summary>
/// Self-contained Shell_NotifyIcon wrapper (replaces the WinForms NotifyIcon dependency).
/// Creates a hidden message window, exposes left-double-click / right-click events and
/// lets the app push drawn HICONs, tooltip text and balloon notifications.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WM_APP_TRAY = 0x8000; // WM_APP
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_ERROR = 3;
    private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;

    private HwndSource? _source;
    private bool _added;

    public event Action? DoubleClick;
    public event Action? RightClick;

    public TrayIconService()
    {
        var parameters = new HwndSourceParameters("MemoryToolTrayWindow", 1, 1)
        {
            WindowStyle = 0, // WS_OVERLAPPED-less: hidden message-only-ish window
            PositionX = -32000,
            PositionY = -32000
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_APP_TRAY) return IntPtr.Zero;
        handled = true;
        switch (lParam.ToInt32())
        {
            case WM_LBUTTONDBLCLK: DoubleClick?.Invoke(); break;
            case WM_RBUTTONUP: RightClick?.Invoke(); break;
        }
        return IntPtr.Zero;
    }

    private void Modify(int flags, IntPtr hIcon, string? tip, string? balloonTitle, string? balloonText, int balloonFlags)
    {
        if (_source == null) return;
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = hIcon,
            szTip = tip ?? "",
            szInfo = balloonText ?? "",
            szInfoTitle = balloonTitle ?? "",
            dwInfoFlags = balloonFlags
        };
        if (!_added)
        {
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
        }
        else
        {
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void SetIcon(IntPtr hIcon) => Modify(NIF_MESSAGE | NIF_ICON, hIcon, null, null, null, 0);

    public void SetTooltip(string text)
    {
        if (text.Length > 127) text = text[..127];
        Modify(NIF_MESSAGE | NIF_TIP, IntPtr.Zero, text, null, null, 0);
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        Modify(NIF_INFO, IntPtr.Zero, null, title, text,
            warning ? NIIF_WARNING : text.StartsWith("失败") ? NIIF_ERROR : NIIF_INFO);
    }

    public void Dispose()
    {
        if (_added && _source != null)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _source.Handle,
                uID = 1
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion; // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);
}
