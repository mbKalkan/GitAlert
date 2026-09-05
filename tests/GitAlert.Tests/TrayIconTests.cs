using GitAlert.Platform;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Which shell callback stands for which gesture. Under the version 4 contract the shell sends
/// the legacy mouse message and the richer notification for one click; counting both opened the
/// tray menu twice and toggled the flyout shut again.
/// </summary>
public class TrayIconTests
{
    [Theory]
    [InlineData(NativeMethods.WM_CONTEXTMENU, TrayIcon.TrayGesture.ContextMenu)]
    [InlineData(NativeMethods.WM_RBUTTONUP, TrayIcon.TrayGesture.None)]
    [InlineData(NativeMethods.NIN_SELECT, TrayIcon.TrayGesture.Activate)]
    [InlineData(NativeMethods.NIN_KEYSELECT, TrayIcon.TrayGesture.Activate)]
    [InlineData(NativeMethods.WM_LBUTTONUP, TrayIcon.TrayGesture.None)]
    [InlineData(NativeMethods.NIN_BALLOONUSERCLICK, TrayIcon.TrayGesture.NotificationClick)]
    public void Under_version_4_only_the_richer_message_of_each_pair_counts(int notification, TrayIcon.TrayGesture expected) =>
        Assert.Equal(expected, TrayIcon.Classify(notification, version4: true));

    [Theory]
    [InlineData(NativeMethods.WM_RBUTTONUP, TrayIcon.TrayGesture.ContextMenu)]
    [InlineData(NativeMethods.WM_LBUTTONUP, TrayIcon.TrayGesture.Activate)]
    [InlineData(NativeMethods.WM_CONTEXTMENU, TrayIcon.TrayGesture.None)]
    [InlineData(NativeMethods.NIN_SELECT, TrayIcon.TrayGesture.None)]
    [InlineData(NativeMethods.NIN_BALLOONUSERCLICK, TrayIcon.TrayGesture.NotificationClick)]
    public void Without_the_contract_the_legacy_messages_are_all_there_is(int notification, TrayIcon.TrayGesture expected) =>
        Assert.Equal(expected, TrayIcon.Classify(notification, version4: false));
}
