using GitAlert.Core;

namespace GitAlert.Platform;

/// <summary>What the icon in the notification area is telling the user at a glance.</summary>
public enum TrayState
{
    Idle,
    Unread,
    Warning,
    Error,
}

public enum NotificationKind
{
    None,
    Info,
    Warning,
    Error,
}

/// <summary>One line of the tray icon's menu; <see cref="Separator"/> draws a rule instead.</summary>
public sealed record TrayMenuEntry(string Header, Action? Invoke, bool IsDefault = false)
{
    public static readonly TrayMenuEntry Separator = new(string.Empty, null);

    public bool IsSeparator => Invoke is null;
}

/// <summary>
/// The notification-area icon and the notifications that come from it, however the platform
/// provides them: <c>Shell_NotifyIcon</c> and its balloons on Windows, a status item and the
/// notification centre elsewhere.
/// </summary>
public interface ITrayHost : IDisposable
{
    /// <summary>Left click or keyboard activation; carries the screen point of the icon.</summary>
    event EventHandler<ScreenPoint>? Activated;

    /// <summary>Right click; carries the screen point to anchor a menu at.</summary>
    event EventHandler<ScreenPoint>? ContextMenuRequested;

    /// <summary>The user clicked the notification itself.</summary>
    event EventHandler? NotificationClicked;

    string Tooltip { get; set; }

    void SetState(TrayState state, bool hasUnread);

    /// <summary>Redraws the icon for the current size and system theme.</summary>
    void Refresh();

    void ShowNotification(string title, string message, NotificationKind kind, bool playSound);

    /// <summary>
    /// The menu behind a right click, for the platforms whose status item shows a menu of its own.
    /// Windows raises <see cref="ContextMenuRequested"/> instead and the shell draws the menu, so
    /// its host ignores this.
    /// </summary>
    void SetMenu(IReadOnlyList<TrayMenuEntry> entries);
}
