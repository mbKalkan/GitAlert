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

/// <summary>An issue on screen: picked, it fills the pane with what it says rather than with a diff.</summary>
public class AlertCardTests
{
    [AvaloniaFact]
    public void Picking_an_issue_shows_what_it_says_its_labels_and_the_way_to_it()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = BuildFlyout();

        try
        {
            window.Show();
            Frames.Settle();

            var alert = vm.Alerts.Single(a => a.Kind == AlertKind.Issue);
            vm.SelectAlertCommand.Execute(alert);
            Frames.Settle();

            var card = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "AlertCard");
            Assert.True(card.IsEffectivelyVisible);

            var texts = card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Issue #77 opened", texts);
            Assert.Contains("Rate limit the poller when GitHub throttles", texts);
            Assert.Contains(texts, t => t is not null && t.StartsWith("acme / api-gateway · by mbKalkan · ", StringComparison.Ordinal));
            Assert.Contains("Labels", texts);
            Assert.Contains("bug, api", texts);

            var body = card.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            Assert.True(body.IsEffectivelyVisible);
            Assert.Equal(SampleData.IssueBody, body.Text);

            var buttons = card.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToList();
            Assert.Contains("Open issue", buttons);
            Assert.Contains("Open repository", buttons);

            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void An_alert_that_said_nothing_shows_no_empty_box()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = BuildFlyout();

        try
        {
            window.Show();
            Frames.Settle();

            var run = vm.Alerts.Single(a => a.Kind == AlertKind.Workflow);
            vm.SelectAlertCommand.Execute(run);
            Frames.Settle();

            var card = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "AlertCard");
            Assert.True(card.IsEffectivelyVisible);
            Assert.False(card.GetVisualDescendants().OfType<SelectableTextBlock>().Single().IsEffectivelyVisible);

            var buttons = card.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content?.ToString()).ToList();
            Assert.Equal(["Open run", "Open repository"], buttons);

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
        var settings = SampleData.Settings(account);

        var store = new AlertStore(Path.Combine(work, "history.json"));
        store.Add(SampleData.Alerts(account));

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
}
