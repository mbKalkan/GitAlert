using System.Runtime.Versioning;
using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.Platform.MacOS;

/// <summary>
/// Tokens in the user's login keychain, one generic password per account, through the
/// <c>security</c> tool that ships with macOS. The token itself travels over the tool's standard
/// input, never on a command line.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretStore : ISecretStore
{
    private const string Service = "GitAlert";

    private readonly SecretIndex _index = new(Path.Combine(AppPaths.DataDirectory, "keychain-accounts.txt"));

    public string StorageNote => "Tokens are kept in your login keychain, where only you can read them.";

    public bool Has(string accountId) => Read(accountId) is not null;

    public string? Read(string accountId)
    {
        if (!SecureTokenStore.IsValidAccountId(accountId))
        {
            return null;
        }

        var (code, output) = Tool.Run("security", ["find-generic-password", "-a", accountId, "-s", Service, "-w"]);
        return code == 0 && output.Length > 0 ? output : null;
    }

    public void Write(string accountId, string token)
    {
        if (!SecureTokenStore.IsValidAccountId(accountId))
        {
            throw new ArgumentException($"'{accountId}' is not a usable account id.", nameof(accountId));
        }

        // The command is fed to the tool's own prompt, where quoting is its business; a token is
        // letters, digits and underscores, so nothing here needs escaping, and anything else is
        // refused rather than risked.
        if (token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
        {
            throw new InvalidOperationException("The access token contains characters GitAlert cannot store.");
        }

        var command = $"add-generic-password -a \"{accountId}\" -s \"{Service}\" -U -w \"{token}\"\n";
        var (code, _) = Tool.Run("security", ["-i"], standardInput: command);

        // The interactive prompt's exit code is not the command's; reading the item back is.
        if (code != 0 || !string.Equals(Read(accountId), token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The access token could not be stored in the keychain.");
        }

        _index.Add(accountId);
    }

    public void Delete(string accountId)
    {
        if (!SecureTokenStore.IsValidAccountId(accountId))
        {
            return;
        }

        Tool.Run("security", ["delete-generic-password", "-a", accountId, "-s", Service]);
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

    // The single-token layout predates the macOS build, so there is never anything to migrate.

    public bool HasLegacy => false;

    public string? ReadLegacy() => null;

    public void DeleteLegacy()
    {
    }
}
