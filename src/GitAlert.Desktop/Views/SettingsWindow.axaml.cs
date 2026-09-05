using Avalonia.Controls;
using Avalonia.Input;
using GitAlert.Platform;
using GitAlert.Services;
using GitAlert.ViewModels;

namespace GitAlert.Views;

public partial class SettingsWindow : Window
{
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
