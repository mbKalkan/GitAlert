using System.Runtime.InteropServices;
using GitAlert.Core;

namespace GitAlert.Platform;

/// <summary>
/// A notification-area icon built directly on <c>Shell_NotifyIcon</c>. Going native rather than
/// borrowing WinForms' <c>NotifyIcon</c> makes room for the details that matter in practice:
/// per-monitor-crisp icons, a state badge, and recovery when Explorer restarts. It knows no UI
/// framework: the caller supplies the pixels, and the thread's own message loop delivers the clicks.
/// </summary>
public sealed class TrayIcon : ITrayHost
{
    private const int CallbackMessage = NativeMethods.WM_APP + 1;
    private const int IconId = 1;
    private const int MaxRetries = 15;

    /// <summary>
    /// One click on the icon does not always reach us as one message. Depending on the Windows
    /// build the shell can follow <c>NIN_SELECT</c> with the legacy <c>WM_LBUTTONUP</c> for the
    /// same click, and since the click is a toggle the second message closed what the first had
    /// just opened - the flyout appeared and vanished again. Anything this close behind the
    /// previous activation is that echo rather than a second click.
    /// </summary>
    private static readonly TimeSpan ActivationEcho = TimeSpan.FromMilliseconds(250);

    /// <summary>At logon the shell is often not listening yet; this is how long to wait between tries.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly Func<int, Rgb, Rgb?, byte[]> _render;
    private readonly UiThread _ui;
    private readonly MessageWindow _window;
    private readonly uint _taskbarCreatedMessage;
    private readonly Timer _retryTimer;

    private NativeMethods.NOTIFYICONDATA _data;
    private IntPtr _iconHandle;
    private TrayState _state = TrayState.Idle;
    private bool _hasUnread;
    private string _tooltip = "GitAlert";
    private bool _added;
    private bool _disposed;
    private int _retries;
    private bool _version4;
    private DateTime _lastActivation = DateTime.MinValue;

    /// <param name="render">
    /// Draws the icon: the size in pixels, the taskbar's contrast colour, and the badge colour when
    /// there is one. Returns premultiplied BGRA pixels, top-down.
    /// </param>
    public TrayIcon(Func<int, Rgb, Rgb?, byte[]> render)
    {
        _render = render;
        _ui = UiThread.Capture();
        _window = new MessageWindow("GitAlert.TrayIcon", WndProc);

        // Explorer broadcasts this after a crash or restart; the icon must be re-added.
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        // The timer fires on the pool; the shell call goes back to the thread that owns the window.
        _retryTimer = new Timer(_ => _ui.Post(TryAdd), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
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

    public event EventHandler<ScreenPoint>? Activated;

    public event EventHandler<ScreenPoint>? ContextMenuRequested;

    public event EventHandler? NotificationClicked;

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
    public void ShowNotification(string title, string message, NotificationKind kind, bool playSound)
    {
        if (!_added)
        {
            return;
        }

        _data.szInfoTitle = Truncate(title, 63);
        _data.szInfo = Truncate(message, 255);
        _data.dwInfoFlags = kind switch
        {
            NotificationKind.Info => NativeMethods.NIIF_INFO,
            NotificationKind.Warning => NativeMethods.NIIF_WARNING,
            NotificationKind.Error => NativeMethods.NIIF_ERROR,
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

    /// <summary>The shell draws its own menu on <see cref="ContextMenuRequested"/>; nothing to hand over.</summary>
    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
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
            _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Opt into the version 4 callback contract: richer messages and screen coordinates.
            // Whether the shell agreed decides which of its duplicate messages count below.
            _data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
            _version4 = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref _data);

            TraceLog.Write($"tray icon added after {_retries} retries");
            return;
        }

        // Back off and try again rather than leaving the user without an icon.
        if (++_retries <= MaxRetries)
        {
            TraceLog.Write($"tray icon not added (error {Marshal.GetLastWin32Error()}); retry {_retries}");
            _retryTimer.Change(RetryDelay, Timeout.InfiniteTimeSpan);
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
            (TrayState.Error, _) => new Rgb(0xF8, 0x51, 0x49),
            (TrayState.Warning, _) => new Rgb(0xD2, 0x99, 0x22),
            (_, true) => new Rgb(0x3F, 0xB9, 0x50),
            _ => (Rgb?)null,
        };

        var size = IconFactory.TraySize;
        _iconHandle = IconFactory.CreateHIcon(_render(size, SystemTheme.TrayForeground, badge), size, size);
        _data.hIcon = _iconHandle;

        IconFactory.DestroyIcon(previous);
    }

    private IntPtr? WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _added = false;
            _retries = 0;
            RefreshIcon();
            TryAdd();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
        {
            return null;
        }

        // Version 4 packs the event into lParam's low word and the cursor into wParam.
        var notification = LowWord(lParam);
        var point = new ScreenPoint(SignedLowWord(wParam), SignedHighWord(wParam));

        TraceLog.Write($"tray callback 0x{notification:X4} at {point.X},{point.Y}");

        switch (Classify(notification, _version4))
        {
            case TrayGesture.Activate:
                var now = DateTime.UtcNow;

                if (now - _lastActivation < ActivationEcho)
                {
                    TraceLog.Write("  ignored: echo of the previous activation");
                    return IntPtr.Zero;
                }

                _lastActivation = now;
                Activated?.Invoke(this, point);
                return IntPtr.Zero;

            case TrayGesture.ContextMenu:
                ContextMenuRequested?.Invoke(this, point);
                return IntPtr.Zero;

            case TrayGesture.NotificationClick:
                NotificationClicked?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;

            default:
                return null;
        }
    }

    /// <summary>What a shell callback asks for, or nothing.</summary>
    public enum TrayGesture
    {
        None,
        Activate,
        ContextMenu,
        NotificationClick,
    }

    /// <summary>
    /// Sorts a callback into the gesture it stands for. Under the version 4 contract the shell
    /// sends the legacy mouse message and the richer notification for the same click, so only the
    /// richer one counts there: a right click used to open the menu twice, once per message. Without
    /// the contract the legacy messages are all there is.
    /// </summary>
    internal static TrayGesture Classify(int notification, bool version4) => notification switch
    {
        NativeMethods.NIN_SELECT or NativeMethods.NIN_KEYSELECT when version4 => TrayGesture.Activate,
        NativeMethods.WM_LBUTTONUP when !version4 => TrayGesture.Activate,
        NativeMethods.WM_CONTEXTMENU when version4 => TrayGesture.ContextMenu,
        NativeMethods.WM_RBUTTONUP when !version4 => TrayGesture.ContextMenu,
        NativeMethods.NIN_BALLOONUSERCLICK => TrayGesture.NotificationClick,
        _ => TrayGesture.None,
    };

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
        _retryTimer.Dispose();

        if (_added)
        {
            _data.uFlags = 0;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _data);
            _added = false;
        }

        IconFactory.DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;

        _window.Dispose();
    }
}
