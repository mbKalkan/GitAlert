using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GitAlert.Platform;

public enum TrayState
{
    Idle,
    Unread,
    Warning,
    Error,
}

public enum BalloonIcon
{
    None,
    Info,
    Warning,
    Error,
}

/// <summary>
/// A notification-area icon built directly on <c>Shell_NotifyIcon</c>. Going native rather than
/// borrowing WinForms' <c>NotifyIcon</c> keeps the app WPF-only and makes room for the details that
/// matter in practice: per-monitor-crisp icons, a state badge, and recovery when Explorer restarts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = NativeMethods.WM_APP + 1;
    private const int IconId = 1;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreatedMessage;
    private readonly DispatcherTimer _retryTimer;

    private NativeMethods.NOTIFYICONDATA _data;
    private IntPtr _iconHandle;
    private TrayState _state = TrayState.Idle;
    private bool _hasUnread;
    private string _tooltip = "GitAlert";
    private bool _added;
    private bool _disposed;
    private int _retries;

    public TrayIcon()
    {
        var parameters = new HwndSourceParameters("GitAlert.TrayIcon")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            Width = 0,
            Height = 0,
        };

        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);

        // Explorer broadcasts this after a crash or restart; the icon must be re-added.
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += (_, _) => TryAdd();

        _data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = IconId,
            uCallbackMessage = CallbackMessage,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        RefreshIcon();
        TryAdd();
    }

    /// <summary>Left click or keyboard activation; carries the screen point of the icon.</summary>
    public event EventHandler<Point>? Activated;

    /// <summary>Right click; carries the screen point to anchor a menu at.</summary>
    public event EventHandler<Point>? ContextMenuRequested;

    /// <summary>The user clicked the toast itself.</summary>
    public event EventHandler? BalloonClicked;

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            // Windows truncates anything past 127 characters plus a terminator.
            var text = value.Length > 127 ? value[..127] : value;

            if (_tooltip == text)
            {
                return;
            }

            _tooltip = text;
            _data.szTip = text;
            Update(NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        }
    }

    public void SetState(TrayState state, bool hasUnread)
    {
        if (_state == state && _hasUnread == hasUnread)
        {
            return;
        }

        _state = state;
        _hasUnread = hasUnread;
        RefreshIcon();
        Update(NativeMethods.NIF_ICON);
    }

    /// <summary>
    /// Redraws the icon at the current size and taskbar contrast colour. Called when the user
    /// switches their Windows theme.
    /// </summary>
    public void Refresh()
    {
        RefreshIcon();
        Update(NativeMethods.NIF_ICON);
    }

    /// <summary>
    /// Shows a balloon, which Windows 10 and 11 render as a toast and keep in the Action Centre.
    /// </summary>
    public void ShowBalloon(string title, string message, BalloonIcon icon, bool playSound)
    {
        if (!_added)
        {
            return;
        }

        _data.szInfoTitle = Truncate(title, 63);
        _data.szInfo = Truncate(message, 255);
        _data.dwInfoFlags = icon switch
        {
            BalloonIcon.Info => NativeMethods.NIIF_INFO,
            BalloonIcon.Warning => NativeMethods.NIIF_WARNING,
            BalloonIcon.Error => NativeMethods.NIIF_ERROR,
            _ => NativeMethods.NIIF_NONE,
        };

        if (!playSound)
        {
            _data.dwInfoFlags |= NativeMethods.NIIF_NOSOUND;
        }

        Update(NativeMethods.NIF_INFO);

        // Leave the struct clean so a later NIM_MODIFY does not replay the balloon.
        _data.szInfo = string.Empty;
        _data.szInfoTitle = string.Empty;
        _data.dwInfoFlags = NativeMethods.NIIF_NONE;
    }

    private void TryAdd()
    {
        if (_disposed)
        {
            return;
        }

        _data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;

        if (NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data))
        {
            _added = true;
            _retryTimer.Stop();

            // Opt into the version 4 callback contract: richer messages and screen coordinates.
            _data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);
            return;
        }

        // At logon the shell is often not listening yet, so back off and try again rather than
        // leaving the user without an icon.
        if (++_retries <= 15)
        {
            _retryTimer.Start();
        }
    }

    private void Update(int flags)
    {
        if (!_added)
        {
            return;
        }

        _data.uFlags = flags;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref _data);
    }

    private void RefreshIcon()
    {
        var previous = _iconHandle;

        var badge = (_state, _hasUnread) switch
        {
            (TrayState.Error, _) => Color.FromRgb(0xF8, 0x51, 0x49),
            (TrayState.Warning, _) => Color.FromRgb(0xD2, 0x99, 0x22),
            (_, true) => Color.FromRgb(0x3F, 0xB9, 0x50),
            _ => (Color?)null,
        };

        _iconHandle = IconFactory.CreateTrayIcon(IconFactory.TraySize, SystemTheme.TrayForeground, badge);
        _data.hIcon = _iconHandle;

        IconFactory.DestroyIcon(previous);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _added = false;
            _retries = 0;
            RefreshIcon();
            TryAdd();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        // Version 4 packs the event into lParam's low word and the cursor into wParam.
        var notification = LowWord(lParam);
        var point = new Point(SignedLowWord(wParam), SignedHighWord(wParam));

        switch (notification)
        {
            case NativeMethods.NIN_SELECT:
            case NativeMethods.NIN_KEYSELECT:
            case NativeMethods.WM_LBUTTONUP:
                Activated?.Invoke(this, point);
                handled = true;
                break;

            case NativeMethods.WM_CONTEXTMENU:
            case NativeMethods.WM_RBUTTONUP:
                ContextMenuRequested?.Invoke(this, point);
                handled = true;
                break;

            case NativeMethods.NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private static int LowWord(IntPtr value) => (int)((long)value & 0xFFFF);

    private static int SignedLowWord(IntPtr value) => (short)((long)value & 0xFFFF);

    private static int SignedHighWord(IntPtr value) => (short)(((long)value >> 16) & 0xFFFF);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _retryTimer.Stop();

        if (_added)
        {
            _data.uFlags = 0;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _data);
            _added = false;
        }

        IconFactory.DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;

        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
