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

    private static bool IsShown(SettingsWindow window, string pageTitle) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(t => t.Text == pageTitle && t.Classes.Contains("pageTitle") && t.IsEffectivelyVisible);

    private static (SettingsWindow Window, SettingsViewModel ViewModel, Action Dispose) Build()
    {
        var (window, vm, _, dispose) = BuildWithStore();
        return (window, vm, dispose);
    }

    private static (SettingsWindow Window, SettingsViewModel ViewModel, SettingsStore Store, Action Dispose) BuildWithStore()
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settingsStore = new SettingsStore(Path.Combine(work, "settings.json"));
        settingsStore.Save(SampleData.Settings(account));

        var tokens = new SecureTokenStore(new PlainProtector(), work);
        tokens.Write(account.Id, "ghp_sample");

        var vm = new SettingsViewModel(settingsStore, tokens, new NoShell(), new NoShell());
        var window = new SettingsWindow(vm, new HeadlessPlatform(), new ThemeService(Avalonia.Application.Current!));

        return (window, vm, settingsStore, () =>
        {
            window.Close();
            vm.Dispose();
        });
    }
}
