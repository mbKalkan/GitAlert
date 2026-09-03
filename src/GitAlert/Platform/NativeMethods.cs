using System.Runtime.InteropServices;

namespace GitAlert.Platform;

/// <summary>
/// The Win32 surface GitAlert touches: the notification area, icon creation and a couple of
/// window-management calls. Kept in one file so the interop is easy to audit.
/// </summary>
internal static class NativeMethods
{
    // ---- Notification area -------------------------------------------------

    public const int NIM_ADD = 0x00;
    public const int NIM_MODIFY = 0x01;
    public const int NIM_DELETE = 0x02;
    public const int NIM_SETVERSION = 0x04;

    public const int NIF_MESSAGE = 0x01;
    public const int NIF_ICON = 0x02;
    public const int NIF_TIP = 0x04;
    public const int NIF_STATE = 0x08;
    public const int NIF_INFO = 0x10;
    public const int NIF_SHOWTIP = 0x80;

    public const int NIIF_NONE = 0x00;
    public const int NIIF_INFO = 0x01;
    public const int NIIF_WARNING = 0x02;
    public const int NIIF_ERROR = 0x03;
    public const int NIIF_USER = 0x04;
    public const int NIIF_NOSOUND = 0x10;
    public const int NIIF_LARGE_ICON = 0x20;

    public const int NOTIFYICON_VERSION_4 = 4;

    // ---- Window messages ---------------------------------------------------

    public const int WM_APP = 0x8000;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;

    public const int NIN_SELECT = 0x0400;
    public const int NIN_KEYSELECT = 0x0401;
    public const int NIN_BALLOONSHOW = 0x0402;
    public const int NIN_BALLOONHIDE = 0x0403;
    public const int NIN_BALLOONTIMEOUT = 0x0404;
    public const int NIN_BALLOONUSERCLICK = 0x0405;

    /// <summary>Message-only windows never appear on screen and receive no broadcast messages.</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        /// <summary>Union of uTimeout and uVersion; only the version meaning is used here.</summary>
        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- Foreground activation ---------------------------------------------

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint thread, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>
    /// Brings a window to the foreground even when Windows would normally refuse.
    /// <c>SetForegroundWindow</c> only obeys the process that already owns the foreground or that
    /// received the last input event, and a click on a tray icon satisfies neither: Explorer got
    /// the click, not us. Left at that, the flyout is activated by our own thread but never by the
    /// system, so Windows takes the activation straight back and the panel blinks shut on its own.
    /// Briefly sharing an input queue with the foreground thread is the documented way past it.
    /// </summary>
    public static bool ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        // SetForegroundWindow reports success even when Windows only flashed the taskbar button
        // instead of handing the foreground over, so the result is worth nothing here. Ask who is
        // actually in front.
        SetForegroundWindow(hWnd);

        if (IsForeground(hWnd))
        {
            return true;
        }

        var self = GetCurrentThreadId();
        var owner = GetForegroundWindow() is var foreground && foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, IntPtr.Zero)
            : 0u;

        if (owner != 0 && owner != self && AttachThreadInput(self, owner, true))
        {
            try
            {
                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
                SetActiveWindow(hWnd);
            }
            finally
            {
                AttachThreadInput(self, owner, false);
            }
        }
        else
        {
            BringWindowToTop(hWnd);
            SetActiveWindow(hWnd);
        }

        return IsForeground(hWnd);
    }

    /// <summary>True when the given window is the one Windows currently considers foreground.</summary>
    public static bool IsForeground(IntPtr hWnd) => hWnd != IntPtr.Zero && GetForegroundWindow() == hWnd;

    // ---- Icon creation -----------------------------------------------------

    public const int BI_RGB = 0;
    public const int DIB_RGB_COLORS = 0;

    public const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO bmi,
        int usage,
        out IntPtr bits,
        IntPtr section,
        int offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int width, int height, int planes, int bitCount, byte[]? bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);

    // ---- Monitors ----------------------------------------------------------

    public const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    // ---- Desktop Window Manager -------------------------------------------

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 10 2004 and later.</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>The attribute number the same setting had on earlier Windows 10 builds.</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    /// <summary>
    /// Asks DWM to draw the title bar in the dark palette. Older builds only accept the legacy
    /// attribute number, so both are tried and any failure is simply ignored.
    /// </summary>
    public static void SetTitleBarTheme(IntPtr hWnd, bool dark)
    {
        var value = dark ? 1 : 0;

        try
        {
            if (DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref value, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // No DWM: the title bar simply stays light.
        }
    }

}
