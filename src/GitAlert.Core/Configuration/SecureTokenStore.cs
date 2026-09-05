using System.Text;
using GitAlert.Core;
using GitAlert.Platform;

namespace GitAlert.Configuration;

/// <summary>
/// Keeps each account's token in its own file, named by the account id, which keeps a work token and
/// a personal token completely separate. What goes into the file is the <see cref="ITokenProtector"/>'s
/// business: on Windows a DPAPI blob bound to the signed-in user, so a copy is useless anywhere else.
/// </summary>
public sealed class SecureTokenStore : ISecretStore
{
    private readonly ITokenProtector _protector;
    private readonly string _directory;
    private readonly string _legacyFile;

    public SecureTokenStore(ITokenProtector protector, string? dataDirectory = null)
    {
        var root = dataDirectory ?? AppPaths.DataDirectory;

        _protector = protector;
        _directory = Path.Combine(root, "tokens");
        _legacyFile = Path.Combine(root, "token.bin");
    }

    /// <summary>
    /// Whether an account id may be used as a file name.
    /// </summary>
    /// <remarks>
    /// Ids are generated as a bare GUID and never typed, but they arrive here by way of
    /// settings.json, which is a text file in a folder the user can open. An id of
    /// <c>..\..\something</c> would put a token wherever the process can write. There is no
    /// legitimate id outside this shape, so the answer is to refuse one rather than to repair it.
    /// </remarks>
    public static bool IsValidAccountId(string? accountId) =>
        accountId is { Length: > 0 and <= 64 }
        && accountId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public bool Has(string accountId) => PathFor(accountId) is { } path && File.Exists(path);

    public string? Read(string accountId) => PathFor(accountId) is { } path ? ReadFile(path) : null;

    public void Write(string accountId, string token)
    {
        var path = PathFor(accountId)
            ?? throw new ArgumentException($"'{accountId}' is not a usable account id.", nameof(accountId));

        Directory.CreateDirectory(_directory);

        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(token))
            ?? throw new InvalidOperationException("The access token could not be encrypted.");

        File.WriteAllBytes(path, protectedBytes);
    }

    public void Delete(string accountId)
    {
        if (PathFor(accountId) is not { } path || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are saved before tokens are deleted, so a file that will not go is
            // already orphaned: no account names it, and Prune will try again on the next save.
        }
    }

    /// <summary>Reads every stored token, keyed by account id, skipping any that fail to decrypt.</summary>
    public Dictionary<string, string> ReadAll(IEnumerable<string> accountIds)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var id in accountIds)
        {
            if (Read(id) is { } token)
            {
                tokens[id] = token;
            }
        }

        return tokens;
    }

    /// <summary>Removes token files that no longer belong to any configured account.</summary>
    public void Prune(IEnumerable<string> keep)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var live = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(_directory, "*.bin"))
        {
            if (!live.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A locked or read-only file simply lingers; it is inert either way.
                }
            }
        }
    }

    // ---- Legacy single-token layout ----------------------------------------

    /// <summary>The token written by versions before multi-account support, if one is still there.</summary>
    public string? ReadLegacy() => ReadFile(_legacyFile);

    public bool HasLegacy => File.Exists(_legacyFile);

    public void DeleteLegacy()
    {
        if (File.Exists(_legacyFile))
        {
            File.Delete(_legacyFile);
        }
    }

    private string? PathFor(string accountId) =>
        IsValidAccountId(accountId) ? Path.Combine(_directory, accountId + ".bin") : null;

    private string? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plain = _protector.Unprotect(File.ReadAllBytes(path));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // A blob written by another user account (or a corrupted file) cannot be decrypted.
            // Treat it as "no token" so the user is simply asked again.
            return null;
        }
    }
}
