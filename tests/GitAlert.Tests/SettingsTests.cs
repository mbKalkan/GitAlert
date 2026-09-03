using System.IO;
using GitAlert.Configuration;
using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gitalert-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void A_missing_file_yields_usable_defaults()
    {
        var settings = new SettingsStore(_path).Load();

        Assert.Equal(2, settings.PollIntervalMinutes);
        Assert.True(settings.IncludeInbox);
        Assert.Empty(settings.Repositories);
        Assert.Empty(settings.MutedKinds);
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        var store = new SettingsStore(_path);

        store.Save(new AppSettings
        {
            PollIntervalMinutes = 15,
            Theme = AppTheme.Light,
            MutedKinds = [AlertKind.Star, AlertKind.Fork],
            Repositories = [new RepoSubscription { Owner = "acme", Name = "api", IsPrivate = true }],
        });

        var loaded = store.Load();

        Assert.Equal(15, loaded.PollIntervalMinutes);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.True(loaded.IsMuted(AlertKind.Star));
        Assert.False(loaded.IsMuted(AlertKind.Push));
        Assert.Equal("acme/api", loaded.Repositories[0].FullName);
        Assert.True(loaded.Repositories[0].IsPrivate);
    }

    [Fact]
    public void A_hand_edited_interval_is_clamped_rather_than_trusted()
    {
        var settings = new AppSettings { PollIntervalMinutes = 0, MaxHistory = 5 };
        settings.Normalise();

        Assert.Equal(AppSettings.MinimumPollMinutes, settings.PollIntervalMinutes);
        Assert.Equal(20, settings.MaxHistory);

        settings.PollIntervalMinutes = 10_000;
        settings.Normalise();
        Assert.Equal(AppSettings.MaximumPollMinutes, settings.PollIntervalMinutes);
    }

    [Fact]
    public void Duplicate_repositories_are_collapsed_case_insensitively()
    {
        var settings = new AppSettings
        {
            Repositories =
            [
                new RepoSubscription { Owner = "acme", Name = "api" },
                new RepoSubscription { Owner = "ACME", Name = "API" },
                new RepoSubscription { Owner = "acme", Name = "web" },
            ],
        };

        settings.Normalise();

        Assert.Equal(2, settings.Repositories.Count);
    }

    [Fact]
    public void A_corrupt_settings_file_is_set_aside_instead_of_blocking_startup()
    {
        File.WriteAllText(_path, "not json at all");

        var settings = new SettingsStore(_path).Load();

        Assert.Equal(2, settings.PollIntervalMinutes);
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Clone_is_deep_enough_that_editing_a_copy_is_safe()
    {
        var original = new AppSettings
        {
            Repositories = [new RepoSubscription { Owner = "acme", Name = "api", Enabled = true }],
            MutedKinds = [AlertKind.Star],
        };

        var copy = original.Clone();
        copy.Repositories[0].Enabled = false;
        copy.MutedKinds.Add(AlertKind.Push);

        Assert.True(original.Repositories[0].Enabled);
        Assert.False(original.IsMuted(AlertKind.Push));
    }

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + ".corrupt" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        GC.SuppressFinalize(this);
    }
}
