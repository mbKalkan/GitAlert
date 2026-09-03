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

    /// <summary>Guards against writing the password box back into the view model on load.</summary>
    private bool _syncingToken;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnLoaded;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A PasswordBox deliberately refuses to expose its content to data binding, so the two
        // directions are wired up by hand.
        _syncingToken = true;
        TokenBox.Password = _viewModel.Token;
        _syncingToken = false;

        TokenBox.Focus();
    }

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingToken)
        {
            _viewModel.Token = TokenBox.Password;
        }
    }

    private void OnRepoInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (_viewModel.AddRepositoryCommand.CanExecute(null))
        {
            _viewModel.AddRepositoryCommand.Execute(null);
        }

        e.Handled = true;
    }
}
