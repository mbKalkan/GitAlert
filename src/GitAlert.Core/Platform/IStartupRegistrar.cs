namespace GitAlert.Platform;

/// <summary>Registers GitAlert to start when the user signs in, however the platform does that.</summary>
public interface IStartupRegistrar
{
    /// <summary>Passed on a sign-in launch so the app knows to stay in the tray, silently.</summary>
    const string LaunchArgument = "--startup";

    bool IsEnabled { get; }

    /// <summary>Returns true when the change was applied.</summary>
    bool SetEnabled(bool enabled);
}
