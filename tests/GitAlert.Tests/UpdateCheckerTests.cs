using System.IO;
using System.Net;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.GitHub;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The daily question to GitHub about GitAlert's own newest release: what counts as newer, what
/// the request looks like, and how the answer reaches the window's footer and the About page.
/// </summary>
public class UpdateCheckerTests : IDisposable
{
    private static readonly Version Running = new(2, 5, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gitalert-updates-{Guid.NewGuid():N}");

    // ---- What counts as newer ----------------------------------------------

    [Theory]
    [InlineData("v2.6.0", true)]
    [InlineData("2.6.0", true)]
    [InlineData("v3.0", true)]
    [InlineData("v2.5.1", true)]
    [InlineData("v2.5.0", false)]
    [InlineData("v2.5", false)]
    [InlineData("v2.4.9", false)]
    [InlineData("v1.25.2", false)]
    [InlineData("nightly", false)]
    [InlineData("", false)]
    public void A_release_is_offered_only_when_its_tag_names_a_newer_version(string tag, bool newer)
    {
        var release = new GhRelease { TagName = tag, HtmlUrl = "https://github.com/mbKalkan/GitAlert/releases/tag/" + tag, Name = "Something" };

        var update = UpdateChecker.Compare(release, Running);

        Assert.Equal(newer, update is not null);

        if (update is not null)
        {
            Assert.Equal(release.HtmlUrl, update.Url);
            Assert.Equal("Something", update.Name);
        }
    }

    [Fact]
    public void A_draft_or_a_pre_release_is_not_on_offer_whatever_its_number()
    {
        Assert.Null(UpdateChecker.Compare(new GhRelease { TagName = "v9.0.0", Draft = true }, Running));
        Assert.Null(UpdateChecker.Compare(new GhRelease { TagName = "v9.0.0", Prerelease = true }, Running));
    }

    [Fact]
    public void A_release_without_a_page_of_its_own_opens_the_releases_page()
    {
        var update = UpdateChecker.Compare(new GhRelease { TagName = "v9.0.0" }, Running);

        Assert.Equal(UpdateChecker.ReleasesUrl, update!.Url);
    }

    [Fact]
    public void The_running_version_has_three_numbers_like_a_release_tag()
    {
        Assert.Equal(3, UpdateChecker.CurrentVersion.ToString().Count(c => c == '.') + 1);
        Assert.True(UpdateChecker.CurrentVersion >= new Version(2, 5, 0));
    }

    // ---- The request and the answer ----------------------------------------

    [Fact]
    public async Task The_check_asks_for_the_latest_release_without_a_token_and_announces_what_it_found()
    {
        var handler = new StubHandler(_ => Responses.Ok(Release("v9.1.0")));
        using var checker = new UpdateChecker(new HttpClient(handler), Running);
        var announced = 0;
        checker.Changed += (_, _) => announced++;

        var update = await checker.CheckAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/repos/mbKalkan/GitAlert/releases/latest", request.Path);
        Assert.Null(request.Authorization);

        Assert.NotNull(update);
        Assert.Equal(new Version(9, 1, 0), update.Version);
        Assert.Same(update, checker.Available);
        Assert.Null(checker.LastError);
        Assert.NotNull(checker.LastCheckedAt);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task A_check_that_finds_nothing_newer_says_so_by_leaving_nothing_on_offer()
    {
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(_ => Responses.Ok(Release("v2.5.0")))), Running);

        Assert.Null(await checker.CheckAsync());
        Assert.Null(checker.Available);
        Assert.Null(checker.LastError);
        Assert.NotNull(checker.LastCheckedAt);
    }

    [Fact]
    public async Task A_check_that_cannot_be_answered_keeps_the_reason_rather_than_throwing()
    {
        using var refused = new UpdateChecker(new HttpClient(new StubHandler(_ => Responses.Status(HttpStatusCode.NotFound))), Running);
        using var offline = new UpdateChecker(new HttpClient(new StubHandler(_ => throw new HttpRequestException("no route"))), Running);

        Assert.Null(await refused.CheckAsync());
        Assert.Null(await offline.CheckAsync());

        Assert.NotNull(refused.LastError);
        Assert.Equal("Cannot reach GitHub.", offline.LastError);
        Assert.NotNull(refused.LastCheckedAt);
    }

    [Fact]
    public async Task A_newer_version_found_earlier_is_forgotten_by_a_check_that_no_longer_finds_it()
    {
        var tag = "v9.0.0";
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(_ => Responses.Ok(Release(tag)))), Running);

        Assert.NotNull(await checker.CheckAsync());

        tag = "v2.5.0";
        Assert.Null(await checker.CheckAsync());
        Assert.Null(checker.Available);
    }

    [Fact]
    public void The_switch_is_remembered_and_off_means_nothing_is_asked_on_its_own()
    {
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(_ => throw new InvalidOperationException("nothing should be asked"))), Running);
        var announced = 0;
        checker.Changed += (_, _) => announced++;

        Assert.False(checker.IsEnabled);

        checker.Configure(true);
        checker.Configure(true);
        Assert.True(checker.IsEnabled);
        Assert.Equal(1, announced);

        checker.Configure(false);
        Assert.False(checker.IsEnabled);
        Assert.Equal(2, announced);
    }

    // ---- Where the answer shows --------------------------------------------

    [Fact]
    public async Task The_footer_names_the_new_version_once_a_check_has_found_one()
    {
        var store = new AlertStore(Path.Combine(_root, "history.json"));
        var monitor = new MonitorService(store, new StateStore(Path.Combine(_root, "state.json")), new HttpClient(new StubHandler(_ => throw new InvalidOperationException())));
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(_ => Responses.Ok(Release("v9.1.0")))), Running);
        using var flyout = new FlyoutViewModel(store, monitor, new SilentShell(), new AppSettings(), checker);

        Assert.False(flyout.HasUpdate);

        await checker.CheckAsync();

        Assert.True(flyout.HasUpdate);
        Assert.Equal("Version 9.1.0 is out", flyout.UpdateText);
    }

    [Fact]
    public async Task The_about_page_reports_the_check_and_offers_the_release()
    {
        Directory.CreateDirectory(_root);
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(_ => Responses.Ok(Release("v9.1.0", "Nine point one")))), Running);
        using var settings = new SettingsViewModel(
            new SettingsStore(Path.Combine(_root, "settings.json")),
            new SecureTokenStore(new PlainProtector(), _root),
            new SilentHost(),
            new StartupOff(),
            checker);

        Assert.True(settings.CheckForUpdates, "on by default");
        Assert.False(settings.HasAvailableUpdate);
        Assert.Equal("Checks are off; ask with the button.", settings.UpdateStatus);

        checker.Configure(true);
        Assert.Equal("Not checked yet.", settings.UpdateStatus);

        await settings.CheckForUpdatesNowCommand.ExecuteAsync(null);

        Assert.True(settings.HasAvailableUpdate);
        Assert.Equal("9.1.0", settings.AvailableUpdate);
        Assert.Equal("9.1.0 is out; you have 2.5.0.", settings.UpdateStatus);
        Assert.False(settings.IsCheckingForUpdates);
    }

    [Fact]
    public void The_switch_is_saved_with_the_settings_and_on_by_default()
    {
        Directory.CreateDirectory(_root);
        var store = new SettingsStore(Path.Combine(_root, "settings.json"));

        Assert.True(store.Load().CheckForUpdates);

        using var settings = new SettingsViewModel(store, new SecureTokenStore(new PlainProtector(), _root), new SilentHost(), new StartupOff());
        settings.CheckForUpdates = false;
        settings.SaveCommand.Execute(null);

        Assert.False(store.Load().CheckForUpdates);
        Assert.False(store.Load().Clone().CheckForUpdates);
    }

    // ---- Plumbing ----------------------------------------------------------

    private static string Release(string tag, string? name = null) =>
        $$"""{"tag_name":"{{tag}}","name":"{{name ?? tag}}","html_url":"https://github.com/mbKalkan/GitAlert/releases/tag/{{tag}}","prerelease":false,"draft":false,"published_at":"2026-09-06T10:00:00Z"}""";

    private sealed class SilentShell : IShellCommands
    {
        public void ShowSettings()
        {
        }

        public void HideFlyout()
        {
        }

        public void Quit()
        {
        }

        public void SaveListPreferences(ListPreferences preferences)
        {
        }

        public void UnreadChanged()
        {
        }
    }

    private sealed class SilentHost : ISettingsHost
    {
        public void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens, bool listReplaced)
        {
        }

        public void ResetMonitorState()
        {
        }

        public void ClearHistory()
        {
        }

        public void CloseSettings(bool saved)
        {
        }
    }

    private sealed class StartupOff : GitAlert.Platform.IStartupRegistrar
    {
        public bool IsEnabled => false;

        public bool SetEnabled(bool enabled) => true;
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
            }
        }
    }
}
