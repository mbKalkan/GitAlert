using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitAlert.Configuration;

/// <summary>What an exported settings file says about itself, before the settings themselves.</summary>
public sealed class SettingsExportHeader
{
    /// <summary>The shape of the file. Raised only when an older GitAlert could not read a newer file.</summary>
    public int Format { get; set; } = SettingsPortability.CurrentFormat;

    /// <summary>The GitAlert that wrote it, for the message when the format is too new.</summary>
    public string Version { get; set; } = string.Empty;

    public DateTimeOffset ExportedAt { get; set; }
}

/// <summary>An exported settings file: a header, then the settings as the settings file holds them.</summary>
public sealed class SettingsExport
{
    [JsonPropertyName("gitalert")]
    public SettingsExportHeader? GitAlert { get; set; }

    [JsonPropertyName("settings")]
    public AppSettings? Settings { get; set; }
}

/// <summary>What an import brought, for the message that reports it and the save that keeps it.</summary>
public sealed record SettingsImport(
    AppSettings Settings,
    int Accounts,
    int Repositories,
    int Boards,
    int Sections,
    int AccountsMatchedByLogin,
    string WrittenBy);

/// <summary>A file that is not a GitAlert settings export, or one this build cannot read.</summary>
public sealed class SettingsImportException(string message) : Exception(message);

/// <summary>
/// Moves the settings between machines as one file: the accounts, the repositories and boards
/// under them, the sections and the order of the list, the folds, and every switch. Never a
/// token - those stay in the platform's own store - and never where the window was, which is
/// the other machine's business. On the way in, an account that already exists here by login
/// keeps the id it has, so the token already saved for it goes on working.
/// </summary>
public static class SettingsPortability
{
    public const int CurrentFormat = 1;

    /// <summary>The settings file's own shape, read leniently: a file edited by hand may not mind its case.</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>"GitAlert-settings-2026-09-06.json": what the save dialog offers.</summary>
    public static string SuggestedFileName(DateTimeOffset? now = null) =>
        $"GitAlert-settings-{(now ?? DateTimeOffset.Now):yyyy-MM-dd}.json";

    /// <summary>The settings as a file, without what belongs to this machine alone.</summary>
    public static string Export(AppSettings settings, string version, DateTimeOffset? now = null)
    {
        var portable = settings.Clone();

        // Where the window stood and how it was split are this screen's; whether GitAlert starts
        // at sign-in is this machine's registration, not a choice the file can carry.
        portable.WindowLeft = null;
        portable.WindowTop = null;
        portable.WindowWidth = null;
        portable.WindowHeight = null;
        portable.ListPaneShare = null;
        portable.StartWithWindows = false;

        var bundle = new SettingsExport
        {
            GitAlert = new SettingsExportHeader { Version = version, ExportedAt = now ?? DateTimeOffset.Now },
            Settings = portable,
        };

        return JsonSerializer.Serialize(bundle, Options);
    }

    /// <summary>Writes the export beside a temporary file, so a half-written file is never left behind.</summary>
    public static void ExportTo(string path, AppSettings settings, string version)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, Export(settings, version));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads an export against what this machine has, keeping this machine's window and its
    /// account ids where the logins match. What comes back is not saved; that is the caller's
    /// Save, as with every other change in the settings window.
    /// </summary>
    public static SettingsImport Import(string json, AppSettings current)
    {
        SettingsExport? bundle;

        try
        {
            bundle = JsonSerializer.Deserialize<SettingsExport>(json, Options);
        }
        catch (JsonException)
        {
            throw new SettingsImportException("That is not a GitAlert settings file.");
        }

        if (bundle?.GitAlert is not { } header || bundle.Settings is not { } imported)
        {
            throw new SettingsImportException("That is not a GitAlert settings file.");
        }

        if (header.Format > CurrentFormat)
        {
            var by = string.IsNullOrWhiteSpace(header.Version) ? "a newer GitAlert" : $"GitAlert {header.Version}";
            throw new SettingsImportException($"That file was written by {by}, which this version cannot read. Update GitAlert first.");
        }

        imported.Normalise();

        imported.WindowLeft = current.WindowLeft;
        imported.WindowTop = current.WindowTop;
        imported.WindowWidth = current.WindowWidth;
        imported.WindowHeight = current.WindowHeight;
        imported.ListPaneShare = current.ListPaneShare;
        imported.StartWithWindows = current.StartWithWindows;

        var matched = 0;

        foreach (var account in imported.Accounts)
        {
            var local = current.Accounts.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Login)
                && string.Equals(a.Login, account.Login, StringComparison.OrdinalIgnoreCase));

            if (local is null)
            {
                continue;
            }

            matched++;

            if (string.Equals(local.Id, account.Id, StringComparison.Ordinal))
            {
                continue;
            }

            // The token is filed under the id; keeping this machine's id keeps the token.
            var previous = account.Id;
            account.Id = local.Id;

            foreach (var repository in imported.Repositories.Where(r => string.Equals(r.AccountId, previous, StringComparison.Ordinal)))
            {
                repository.AccountId = local.Id;
            }

            foreach (var board in imported.Boards.Where(b => string.Equals(b.AccountId, previous, StringComparison.Ordinal)))
            {
                board.AccountId = local.Id;
            }
        }

        // Two exported accounts with the same login would now share an id; the second goes.
        imported.Normalise();

        var writtenBy = string.IsNullOrWhiteSpace(header.Version) ? "GitAlert" : $"GitAlert {header.Version}";

        return new SettingsImport(
            imported,
            imported.Accounts.Count,
            imported.Repositories.Count,
            imported.Boards.Count,
            imported.Sections.SelectMany(s => s.SelfAndDescendants()).Count(),
            matched,
            writtenBy);
    }
}
