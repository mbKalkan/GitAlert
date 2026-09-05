using System.Runtime.Versioning;
using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.Platform.Linux;

/// <summary>
/// Tokens in the desktop's Secret Service - GNOME Keyring, KWallet and the like - through
/// libsecret's <c>secret-tool</c>, which reads the secret from its standard input. Where the tool is
/// missing, <see cref="LinuxSecretStores"/> falls back to a file only the user can read.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SecretServiceStore : ISecretStore
{
    /// <summary>The attributes every GitAlert item carries, so a lookup finds exactly one.</summary>
    private static string[] Attributes(string accountId) => ["application", "gitalert", "account", accountId];

    private readonly SecretIndex _index = new(Path.Combine(AppPaths.DataDirectory, "secret-service-accounts.txt"));

    public string StorageNote => "Tokens are kept in your desktop's secret service, alongside your other passwords.";

    public bool Has(string accountId) => Read(accountId) is not null;

    public string? Read(string accountId)
    {
        if (!SecureTokenStore.IsValidAccountId(accountId))
        {
            return null;
        }

        var (code, output) = Tool.Run("secret-tool", ["lookup", .. Attributes(accountId)]);
        return code == 0 && output.Length > 0 ? output : null;
    }

    public void Write(string accountId, string token)
    {
        if (!SecureTokenStore.IsValidAccountId(accountId))
        {
            throw new ArgumentException($"'{accountId}' is not a usable account id.", nameof(accountId));
        }

        var (code, _) = Tool.Run(
            "secret-tool",
            ["store", "--label", $"GitAlert token ({accountId})", .. Attributes(accountId)],
            standardInput: token);

        if (code != 0 || !string.Equals(Read(accountId), token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The access token could not be stored in the secret service.");
        }

        _index.Add(accountId);
    }

    public void Delete(string accountId)
    {
        if (!SecureTokenStore.IsValidAccountId(accountId))
        {
            return;
        }

        Tool.Run("secret-tool", ["clear", .. Attributes(accountId)]);
        _index.Remove(accountId);
    }

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

    public void Prune(IEnumerable<string> keep)
    {
        var live = new HashSet<string>(keep, StringComparer.Ordinal);

        foreach (var id in _index.All.Where(id => !live.Contains(id)))
        {
            Delete(id);
        }
    }

    // The single-token layout predates the Linux build, so there is never anything to migrate.

    public bool HasLegacy => false;

    public string? ReadLegacy() => null;

    public void DeleteLegacy()
    {
    }
}

/// <summary>Picks the best token store this desktop offers.</summary>
[SupportedOSPlatform("linux")]
public static class LinuxSecretStores
{
    public static ISecretStore Create() =>
        Tool.Exists("secret-tool")
            ? new SecretServiceStore()
            : new SecureTokenStore(
                new PlainTokenProtector(),
                storageNote: "Tokens are kept in a file only your user can read. Install libsecret's secret-tool for keychain-grade storage.");
}

/// <summary>
/// No encryption at all: the file's permissions are the only guard. Used where the desktop has no
/// secret service, and said so in the settings window rather than hidden.
/// </summary>
public sealed class PlainTokenProtector : ITokenProtector
{
    public byte[]? Protect(byte[] plain) => plain;

    public byte[]? Unprotect(byte[] cipher) => cipher;
}
