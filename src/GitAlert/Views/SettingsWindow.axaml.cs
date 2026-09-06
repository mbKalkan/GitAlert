using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GitAlert.Platform;
using GitAlert.Services;
using GitAlert.ViewModels;

namespace GitAlert.Views;

public partial class SettingsWindow : Window
{
    /// <summary>The diagnostics page as text, onto the clipboard. The clipboard is the window's, so the copy is made here.</summary>
    private async void OnCopyReportClicked(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard is null)
        {
            _viewModel.Diagnostics.NoteCopyFailed();
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_viewModel.Diagnostics.Report);
            _viewModel.Diagnostics.NoteCopied();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or TimeoutException)
        {
            // The clipboard is another process's on every desktop; when it will not take the text,
            // saying so is all there is to do.
            _viewModel.Diagnostics.NoteCopyFailed();
        }
    }

    private readonly SettingsViewModel _viewModel;
    private readonly IPlatform _platform;
    private readonly ThemeService _theme;

    public SettingsWindow(SettingsViewModel viewModel, IPlatform platform, ThemeService theme)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _platform = platform;
        _theme = theme;
        DataContext = viewModel;

        Opened += (_, _) => _platform.ApplyTitleBarTheme(this, _theme.IsDark);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
