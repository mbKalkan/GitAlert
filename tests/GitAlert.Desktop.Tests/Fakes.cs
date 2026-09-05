using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.ViewModels;

namespace GitAlert.Desktop.Tests;

/// <summary>No tray, no foreground fights, no platform placement: the window as it lays itself out.</summary>
internal sealed class HeadlessPlatform : IPlatform
{
    public ITrayHost CreateTray() => throw new NotSupportedException();

    public ISecretStore CreateSecretStore() => new SecureTokenStore(new PlainProtector(), Path.GetTempPath());

    public IStartupRegistrar Startup { get; } = new NoShell();

    public bool IsSystemDark => true;

    /// <summary>Where the flyout is parked on a first opening; null means "leave it where it is".</summary>
    public PixelPoint? FlyoutPlace { get; set; }

    public PixelPoint? PlaceFlyout(ScreenPoint anchor, PixelSize size) => FlyoutPlace;

    public bool TakeForeground(Window window) => true;

    public bool IsForeground(Window window) => true;

    public void ApplyTitleBarTheme(Window window, bool dark)
    {
    }

    public void RoundCorners(Window window)
    {
    }

    public string StartupProblem => "Could not change the startup entry.";
}

/// <summary>Tokens kept as they are: nothing here leaves the temp folder.</summary>
internal sealed class PlainProtector : ITokenProtector
{
    public byte[]? Protect(byte[] plain) => plain;

    public byte[]? Unprotect(byte[] cipher) => cipher;
}

/// <summary>A shell that answers every request with nothing, and a startup entry that stays off.</summary>
internal sealed class NoShell : IShellCommands, ISettingsHost, IStartupRegistrar
{
    public void ShowSettings()
    {
    }

    public void HideFlyout()
    {
    }

    public void Quit()
    {
    }

    public void SaveListPreferences(IReadOnlyList<string> projectOrder, bool unreadOnly)
    {
    }

    public void UnreadChanged()
    {
    }

    public void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens)
    {
    }

    public void ResetMonitorState()
    {
    }

    public void ClearHistory()
    {
    }

    public void CloseSettings(bool saved)
    {
    }

    public bool IsEnabled => false;

    public bool SetEnabled(bool enabled) => false;
}

/// <summary>Answers the diff requests the detail pane makes with one small commit.</summary>
internal sealed class DiffHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        object body = path.Contains("/commits/")
            ? new
            {
                sha = SampleData.PushSha,
                html_url = "https://github.com",
                commit = new { message = "fix: one thing", author = new { name = "Mert", date = DateTimeOffset.Now } },
                stats = new { additions = 3, deletions = 1, total = 4 },
                files = new object[]
                {
                    new { filename = "src/A.cs", status = "modified", additions = 2, deletions = 1, changes = 3, blob_url = "https://github.com", patch = "@@ -1,2 +1,3 @@\n-old\n+new\n+more\n context" },
                    new { filename = "docs/B.md", status = "added", additions = 1, deletions = 0, changes = 1, blob_url = "https://github.com", patch = "@@ -0,0 +1 @@\n+hello" },
                },
            }
            : new { login = "mbKalkan" };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "4985");
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "5000");
        return Task.FromResult(response);
    }
}

/// <summary>
/// Collects Avalonia's binding complaints while a test runs. A path through a null intermediate
/// is not a mistake - a pane with nothing selected has no selected file - so those are left out.
/// </summary>
internal sealed class BindingErrors : ILogSink, IDisposable
{
    private readonly ILogSink? _previous = Logger.Sink;

    public BindingErrors() => Logger.Sink = this;

    public List<string> Messages { get; } = [];

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (!messageTemplate.Contains("Value is null", StringComparison.Ordinal))
        {
            Messages.Add($"{source}: {messageTemplate}");
        }
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        var message = messageTemplate;

        foreach (var value in propertyValues)
        {
            var open = message.IndexOf('{');
            var close = open >= 0 ? message.IndexOf('}', open) : -1;

            if (close < 0)
            {
                break;
            }

            message = message[..open] + value + message[(close + 1)..];
        }

        Log(level, area, source, message);
    }

    public void Dispose() => Logger.Sink = _previous;
}

/// <summary>Runs the queued layout and render passes the way a real frame would.</summary>
internal static class Frames
{
    public static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
