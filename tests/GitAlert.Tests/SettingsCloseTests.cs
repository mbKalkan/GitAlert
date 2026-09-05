using System.IO;
using GitAlert.Configuration;
using GitAlert.Platform;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// How the settings window hands back to the shell. Opening it tucks the flyout away; a save
/// should bring GitAlert back with the change in view, while a cancel leaves things as they were.
/// </summary>
public class SettingsCloseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gitalert-settings-close-{Guid.NewGuid():N}");

    [Fact]
    public void Save_applies_the_settings_and_asks_for_gitalert_back()
    {
        var host = new RecordingHost();
        using var settings = Build(host);

        settings.SaveCommand.Execute(null);

        Assert.Equal(["apply", "close saved"], host.Calls);
    }

    [Fact]
    public void Cancel_closes_the_settings_and_nothing_else_moves()
    {
        var host = new RecordingHost();
        using var settings = Build(host);

        settings.CancelCommand.Execute(null);

        Assert.Equal(["close"], host.Calls);
    }

    /// <summary>
    /// The refusal used to be reported and the window closed in the same breath, so nobody ever
    /// read it. Everything else is still saved and applied; only the window stays.
    /// </summary>
    [Fact]
    public void A_refused_startup_entry_keeps_the_window_open_with_the_message()
    {
        var host = new RecordingHost();
        using var settings = Build(host, new StartupRefusing());

        settings.StartWithWindows = true;
        settings.SaveCommand.Execute(null);

        Assert.Equal(["apply"], host.Calls);
        Assert.True(settings.IsMessageError);
        Assert.Contains("startup entry", settings.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder that outlives the test is untidy, not a failure.
            }
        }
    }

    private SettingsViewModel Build(ISettingsHost host, IStartupRegistrar? startup = null)
    {
        Directory.CreateDirectory(_root);

        return new SettingsViewModel(
            new SettingsStore(Path.Combine(_root, "settings.json")),
            new SecureTokenStore(new DpapiTokenProtector(), _root),
            host,
            startup ?? new StartupOff());
    }

    /// <summary>A startup entry that cannot be written, the way a locked-down profile answers.</summary>
    private sealed class StartupRefusing : IStartupRegistrar
    {
        public bool IsEnabled => false;

        public bool SetEnabled(bool enabled) => false;
    }

    private sealed class RecordingHost : ISettingsHost
    {
        public List<string> Calls { get; } = [];

        public void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens) => Calls.Add("apply");

        public void ResetMonitorState() => Calls.Add("reset");

        public void ClearHistory() => Calls.Add("clear");

        public void CloseSettings(bool saved) => Calls.Add(saved ? "close saved" : "close");
    }

    private sealed class StartupOff : IStartupRegistrar
    {
        public bool IsEnabled => false;

        public bool SetEnabled(bool enabled) => true;
    }
}
