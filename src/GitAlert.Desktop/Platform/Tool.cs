using System.ComponentModel;
using System.Diagnostics;

namespace GitAlert.Platform;

/// <summary>
/// Runs one of the small command-line tools the non-Windows platforms lean on - <c>security</c>,
/// <c>secret-tool</c>, <c>osascript</c>, <c>notify-send</c> - with arguments passed as a list, so
/// nothing is ever quoted into a shell, and a secret handed over on standard input rather than on
/// a command line every process on the machine can read.
/// </summary>
internal static class Tool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Whether a tool of that name sits on the PATH.</summary>
    public static bool Exists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, name)));
    }

    /// <summary>
    /// Runs the tool to completion. A tool that is not installed, or does not answer in time, is a
    /// failed run rather than an exception: the callers have a sentence for that.
    /// </summary>
    public static (int ExitCode, string Output) Run(
        string file,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);

            if (process is null)
            {
                return (-1, string.Empty);
            }

            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Already gone; nothing to kill.
                }

                return (-1, string.Empty);
            }

            // Both streams are drained either way, so a chatty tool cannot block on a full pipe.
            _ = error.Result;

            return (process.ExitCode, output.Result.TrimEnd('\r', '\n'));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // The tool is not installed, or could not be started at all.
            return (-1, string.Empty);
        }
    }
}
