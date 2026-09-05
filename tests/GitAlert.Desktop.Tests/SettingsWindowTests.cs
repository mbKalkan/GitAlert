using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitAlert.Configuration;
using GitAlert.GitHub;
using GitAlert.Services;
using GitAlert.ViewModels;
using GitAlert.Views;
using Xunit;

namespace GitAlert.Desktop.Tests;

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

    private static bool IsShown(SettingsWindow window, string pageTitle) =>
        window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Any(t => t.Text == pageTitle && t.Classes.Contains("pageTitle") && t.IsEffectivelyVisible);

    private static (SettingsWindow Window, SettingsViewModel ViewModel, Action Dispose) Build()
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settingsStore = new SettingsStore(Path.Combine(work, "settings.json"));
        settingsStore.Save(SampleData.Settings(account));

        var tokens = new SecureTokenStore(new PlainProtector(), work);
        tokens.Write(account.Id, "ghp_sample");

        var vm = new SettingsViewModel(settingsStore, tokens, new NoShell(), new NoShell());
        var window = new SettingsWindow(vm, new HeadlessPlatform(), new ThemeService(Avalonia.Application.Current!));

        return (window, vm, () =>
        {
            window.Close();
            vm.Dispose();
        });
    }
}
