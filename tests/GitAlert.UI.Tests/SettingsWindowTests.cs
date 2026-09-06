using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitAlert.Configuration;
using GitAlert.GitHub;
using GitAlert.Services;
using GitAlert.ViewModels;
using GitAlert.Views;
using Xunit;

namespace GitAlert.UI.Tests;

/// <summary>The settings window: the accounts it lists, the pages it switches between, and clean bindings.</summary>
public class SettingsWindowTests
{
    [AvaloniaFact]
    public void The_accounts_page_lists_each_account_with_its_repositories()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var cards = window.GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is AccountViewModel && b.CornerRadius.TopLeft == 10);
            var rows = window.GetVisualDescendants().OfType<CheckBox>().Count(c => c.DataContext is RepoItemViewModel);

            Assert.Equal(1, cards);
            Assert.Equal(2, rows);
            Assert.Single(vm.Accounts);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void The_navigation_switches_the_page()
    {
        using var errors = new BindingErrors();
        var (window, _, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            Assert.True(IsShown(window, "GitHub accounts"));
            Assert.False(IsShown(window, "General"));

            window.FindControl<ListBox>("Navigation")!.SelectedIndex = 2;
            Frames.Settle();

            Assert.False(IsShown(window, "GitHub accounts"));
            Assert.True(IsShown(window, "General"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Unticking_a_repository_switches_it_off_and_save_keeps_it_off()
    {
        var (window, vm, store, dispose) = BuildWithStore();

        try
        {
            window.Show();
            Frames.Settle();

            var repo = vm.Accounts[0].Repositories.First(r => r.FullName == "acme/api-gateway");
            var box = window.GetVisualDescendants().OfType<CheckBox>().First(c => ReferenceEquals(c.DataContext, repo));
            var tick = box.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(p => p.Name == "Tick");

            Assert.True(repo.IsEnabled);
            Assert.True(tick.IsVisible);

            var at = box.TranslatePoint(new Avalonia.Point(8, 8), window)!.Value;
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Frames.Settle();

            Assert.False(repo.IsEnabled);
            Assert.False(tick.IsVisible);

            vm.SaveCommand.Execute(null);

            var saved = store.Load().Repositories.Single(r => r.FullName == "acme/api-gateway");
            Assert.False(saved.Enabled);
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>A theme of its own gets no context menu from Fluent; the token field lives by paste.</summary>
    [AvaloniaFact]
    public void Every_text_box_offers_paste_on_the_right_button()
    {
        var (window, _, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();

            Assert.NotEmpty(boxes);
            Assert.All(boxes, box =>
            {
                var menu = Assert.IsType<MenuFlyout>(box.ContextFlyout);
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Header as string == "Paste");
            });
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>The Diagnostics page lists what is watched, one row each, with clean bindings.</summary>
    [AvaloniaFact]
    public void The_diagnostics_page_lists_what_is_watched()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = Build(withMonitor: true);

        try
        {
            window.Show();
            window.FindControl<ListBox>("Navigation")!.SelectedIndex = 3;
            Frames.Settle();

            // Two repositories and the account's inbox.
            Assert.True(IsShown(window, "Diagnostics"));
            Assert.Equal(3, vm.Diagnostics.Subjects.Count);

            var rows = window.GetVisualDescendants().OfType<Border>().Count(b => b.Name == "Outcome" && b.IsEffectivelyVisible);
            Assert.Equal(3, rows);

            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Not checked yet");
            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    private static bool IsShown(SettingsWindow window, string pageTitle) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(t => t.Text == pageTitle && t.Classes.Contains("pageTitle") && t.IsEffectivelyVisible);

    private static (SettingsWindow Window, SettingsViewModel ViewModel, Action Dispose) Build(bool withMonitor = false)
    {
        var (window, vm, _, dispose) = BuildWithStore(withMonitor);
        return (window, vm, dispose);
    }

    private static (SettingsWindow Window, SettingsViewModel ViewModel, SettingsStore Store, Action Dispose) BuildWithStore(bool withMonitor = false)
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settings = SampleData.Settings(account);
        var settingsStore = new SettingsStore(Path.Combine(work, "settings.json"));
        settingsStore.Save(settings);

        var tokens = new SecureTokenStore(new PlainProtector(), work);
        tokens.Write(account.Id, "ghp_sample");

        // The diagnostics page reads the monitor; one that has never polled lists what it would check.
        MonitorService? monitor = null;

        if (withMonitor)
        {
            monitor = new MonitorService(
                new AlertStore(Path.Combine(work, "history.json")),
                new StateStore(Path.Combine(work, "state.json")),
                new HttpClient(new DiffHandler()));
            monitor.Configure(settings, new Dictionary<string, string> { [account.Id] = "ghp_sample" });
        }

        var vm = new SettingsViewModel(settingsStore, tokens, new NoShell(), new NoShell(), monitor: monitor);
        var window = new SettingsWindow(vm, new HeadlessPlatform(), new ThemeService(Avalonia.Application.Current!));

        return (window, vm, settingsStore, () =>
        {
            window.Close();
            vm.Dispose();
            monitor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }
}
