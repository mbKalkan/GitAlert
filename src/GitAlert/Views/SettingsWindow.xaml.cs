using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GitAlert.Platform;
using GitAlert.Services;
using GitAlert.ViewModels;

namespace GitAlert.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WPF cannot theme the non-client area, so ask DWM to darken the title bar directly.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            NativeMethods.SetTitleBarTheme(source.Handle, ThemeService.IsDark);
        }
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
