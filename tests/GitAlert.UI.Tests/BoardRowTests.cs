using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using GitAlert.Views;
using Xunit;

namespace GitAlert.UI.Tests;

/// <summary>Boards on screen: the chip, the group with its tag, the card in the pane, the rows in settings.</summary>
public class BoardRowTests
{
    [AvaloniaFact]
    public void A_board_is_a_group_with_a_tag_and_its_alerts_count_under_the_boards_chip()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = BuildFlyout();

        try
        {
            window.Show();
            Frames.Settle();

            var group = vm.Groups.First(g => g.Repository == "acme/#12");
            Assert.True(group.IsBoard);
            Assert.Equal("Roadmap", group.DisplayName);

            var header = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => ReferenceEquals(t.DataContext, group) && t.Inlines is { Count: 3 });
            Assert.Equal("acme/Roadmap  board", header.Inlines!.Text);

            var chip = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChipLabel" && t.Text == "Boards");
            Assert.True(chip.IsEffectivelyVisible);
            Assert.Equal(1, vm.Filters.Single(f => f.Filter == AlertFilter.Boards).Count);

            // No history to page through on a board, so the button offering it stays out of sight.
            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<Button>(),
                b => ReferenceEquals(b.DataContext, group)
                    && b.Content is string label
                    && label.StartsWith("Load", StringComparison.Ordinal)
                    && b.IsEffectivelyVisible);

            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Picking_a_board_alert_shows_the_card_in_the_pane()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = BuildFlyout();

        try
        {
            window.Show();
            Frames.Settle();

            var alert = vm.Alerts.Single(a => a.Kind == AlertKind.Board);
            vm.SelectAlertCommand.Execute(alert);
            Frames.Settle();

            var card = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "BoardCard");
            Assert.True(card.IsEffectivelyVisible);

            var texts = card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Moved to In progress", texts);
            Assert.Contains("#87 Rate limit the poller when GitHub throttles", texts);
            Assert.Contains("acme / Roadmap", texts);
            Assert.Contains("Priority", texts);
            Assert.Contains("P1", texts);

            var buttons = card.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToList();
            Assert.Contains("Open issue", buttons);
            Assert.Contains("Open board", buttons);

            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void The_account_card_lists_its_boards_under_the_repositories()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = BuildSettings();

        try
        {
            window.Show();
            Frames.Settle();

            var rows = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.DataContext is BoardItemViewModel).ToList();
            var row = Assert.Single(rows);
            Assert.True(row.IsEffectivelyVisible);

            var labels = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("BOARDS", labels);
            Assert.Contains("#12 · organisation", labels);
            Assert.Contains("2 repositories · 1 board", labels);

            Assert.Single(Assert.Single(vm.Accounts).Boards);
            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    private static (FlyoutWindow Window, FlyoutViewModel ViewModel, Action Dispose) BuildFlyout()
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settings = SampleData.Settings(account, withBoard: true);

        var store = new AlertStore(Path.Combine(work, "history.json"));
        store.Add([.. SampleData.Alerts(account), SampleData.BoardAlert(account)]);

        var monitor = new MonitorService(
            store,
            new StateStore(Path.Combine(work, "state.json")),
            new HttpClient(new DiffHandler()));

        monitor.Configure(settings, new Dictionary<string, string> { [account.Id] = "ghp_sample" });

        var vm = new FlyoutViewModel(store, monitor, new NoShell(), settings);
        var window = new FlyoutWindow(vm, new HeadlessPlatform());

        return (window, vm, () =>
        {
            window.CloseForGood();
            vm.Dispose();
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    private static (SettingsWindow Window, SettingsViewModel ViewModel, Action Dispose) BuildSettings()
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settingsStore = new SettingsStore(Path.Combine(work, "settings.json"));
        settingsStore.Save(SampleData.Settings(account, withBoard: true));

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
