using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitAlert.Core;

namespace GitAlert.Configuration;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Writes go through a temporary file so a crash
/// mid-save can never leave the user with an unreadable configuration.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
                settings.Normalise();
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Never let a broken file stop the app from starting; keep it for diagnosis.
                TryBackupCorruptFile();
                return new AppSettings();
            }
        }
    }

    /// <summary>
    /// Writes the settings. Returns false when the file could not be written; what is in memory
    /// still stands, and the caller decides whether that is worth telling anyone.
    /// </summary>
    /// <remarks>
    /// Roaming AppData is what OneDrive and backup agents watch, and a file one of them has
    /// open for a moment refuses the rename. That used to surface as an exception out of the
    /// window closing, or out of Save with the window left open and nothing said. Once more
    /// after a beat is nearly always enough; after that it is reported rather than thrown.
    /// </remarks>
    public bool Save(AppSettings settings)
    {
        settings.Normalise();

        lock (_gate)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    AppPaths.EnsureCreated();

                    var json = JsonSerializer.Serialize(settings, SerializerOptions);
                    var temp = _path + ".tmp";

                    File.WriteAllText(temp, json);
                    File.Move(temp, _path, overwrite: true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 2)
                    {
                        return false;
                    }

                    Thread.Sleep(150);
                }
            }
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Best effort only.
        }
    }
}
