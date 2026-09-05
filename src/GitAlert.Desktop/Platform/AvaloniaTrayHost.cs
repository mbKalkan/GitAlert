using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Core;
using GitAlert.Graphics;
using StatusItem = Avalonia.Controls.TrayIcon;

namespace GitAlert.Platform;

/// <summary>
/// The tray icon on macOS and Linux, through Avalonia's own status item: an <c>NSStatusItem</c> on
/// one, a StatusNotifierItem over DBus on the other. Both show a native menu on a right click, so
/// the menu is handed over rather than drawn by GitAlert, and neither says where the icon was
/// clicked, so an activation carries <see cref="NoPoint"/> and the platform picks the corner.
/// </summary>
public sealed class AvaloniaTrayHost : ITrayHost
{
    /// <summary>Drawn large enough for a retina menu bar and scaled down where the panel is smaller.</summary>
    private const int IconSize = 32;

    /// <summary>What an activation carries when the platform has no click position to give.</summary>
    public static readonly ScreenPoint NoPoint = new(int.MinValue, int.MinValue);

    // Aliased: the Windows layer in this namespace has a TrayIcon of its own.
    private readonly StatusItem _icon = new();
    private readonly TrayIcons? _icons;
    private readonly Func<bool> _isDark;
    private readonly Action<string, string, NotificationKind, bool> _notify;

    private TrayState _state = TrayState.Idle;
    private bool _hasUnread;
    private string _tooltip = "GitAlert";
    private bool _disposed;

    /// <param name="isDark">Whether the bar the icon sits in is dark, so the glyph must be light.</param>
    /// <param name="notify">Shows a system notification, however this platform does that.</param>
    public AvaloniaTrayHost(Func<bool> isDark, Action<string, string, NotificationKind, bool> notify)
    {
        _isDark = isDark;
        _notify = notify;

        _icon.Clicked += (_, _) => Activated?.Invoke(this, NoPoint);
        _icon.ToolTipText = _tooltip;
        RefreshIcon();
        _icon.IsVisible = true;

        // The icon lives for as long as it is attached to the application.
        if (Application.Current is { } app)
        {
            _icons = new TrayIcons { _icon };
            StatusItem.SetIcons(app, _icons);
        }
    }

    public event EventHandler<ScreenPoint>? Activated;

    /// <summary>Never raised here: the right click opens the menu handed to <see cref="SetMenu"/>.</summary>
    public event EventHandler<ScreenPoint>? ContextMenuRequested;

    /// <summary>Never raised here: neither notifier reports a click back.</summary>
    public event EventHandler? NotificationClicked;

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            if (_tooltip == value)
            {
                return;
            }

            _tooltip = value;
            _icon.ToolTipText = value;
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
    }

    public void Refresh() => RefreshIcon();

    public void ShowNotification(string title, string message, NotificationKind kind, bool playSound) =>
        _notify(title, message, kind, playSound);

    public void SetMenu(IReadOnlyList<TrayMenuEntry> entries) => _icon.Menu = BuildMenu(entries);

    /// <summary>The native menu for the tray, one item per entry and a rule per separator.</summary>
    public static NativeMenu BuildMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        var menu = new NativeMenu();

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            // A command rather than a Click handler, so a test can fire the item the way the
            // platform's exporter does.
            menu.Add(new NativeMenuItem(entry.Header) { Command = new RelayCommand(entry.Invoke ?? (() => { })) });
        }

        return menu;
    }

    /// <summary>The badge colour for a state: red for an error, amber for a warning, green for unread.</summary>
    internal static Rgb? BadgeFor(TrayState state, bool hasUnread) => (state, hasUnread) switch
    {
        (TrayState.Error, _) => new Rgb(0xF8, 0x51, 0x49),
        (TrayState.Warning, _) => new Rgb(0xD2, 0x99, 0x22),
        (_, true) => new Rgb(0x3F, 0xB9, 0x50),
        _ => null,
    };

    /// <summary>Near-white on a dark bar, near-black on a light one; the Windows layer's choice.</summary>
    internal static Rgb ForegroundFor(bool dark) => dark ? new Rgb(0xF2, 0xF4, 0xF8) : new Rgb(0x1B, 0x1F, 0x26);

    /// <summary>Wraps the bell renderer's premultiplied BGRA pixels in a bitmap the tray can show.</summary>
    internal static WriteableBitmap ToBitmap(byte[] bgra, int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using var buffer = bitmap.Lock();

        var stride = size * 4;

        for (var row = 0; row < size; row++)
        {
            Marshal.Copy(bgra, row * stride, buffer.Address + row * buffer.RowBytes, stride);
        }

        return bitmap;
    }

    private void RefreshIcon()
    {
        var pixels = Bell.RenderTrayIcon(IconSize, ForegroundFor(_isDark()), BadgeFor(_state, _hasUnread));
        _icon.Icon = new WindowIcon(ToBitmap(pixels, IconSize));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.IsVisible = false;
        _icons?.Remove(_icon);
        _icon.Dispose();
    }
}
