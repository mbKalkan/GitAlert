using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GitAlert.Configuration;
using GitAlert.Platform;
using GitAlert.Services;
using GitAlert.ViewModels;

namespace GitAlert.Views;

public partial class SettingsWindow : Window
{
    private static readonly FilePickerFileType JsonFiles = new("GitAlert settings") { Patterns = ["*.json"] };

    /// <summary>
    /// The save dialog, then the file. A file with a path on this machine is written by the view
    /// model, in one move; one behind a portal is written through the stream the picker lends.
    /// </summary>
    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export GitAlert settings",
            SuggestedFileName = SettingsPortability.SuggestedFileName(),
            DefaultExtension = "json",
            FileTypeChoices = [JsonFiles],
            ShowOverwritePrompt = true,
        });

        if (file is null)
        {
            return;
        }

        if (file.TryGetLocalPath() is { } path)
        {
            _viewModel.ExportTo(path);
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_viewModel.BuildExport());
            _viewModel.NoteExported(file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.NoteExportFailed(file.Name, ex.Message);
        }
    }

    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import GitAlert settings",
            AllowMultiple = false,
            FileTypeFilter = [JsonFiles],
        });

        if (files.Count == 0)
        {
            return;
        }

        var file = files[0];

        if (file.TryGetLocalPath() is { } path)
        {
            _viewModel.ImportFrom(path);
            return;
        }

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            _viewModel.Import(await reader.ReadToEndAsync(), file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.NoteImportFailed(file.Name, ex.Message);
        }
    }

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
