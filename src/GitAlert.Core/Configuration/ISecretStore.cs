namespace GitAlert.Configuration;

/// <summary>
/// Where each account's GitHub token lives, keyed by account id. Tokens never enter settings.json;
/// this is the only place that holds them, and how it holds them is the platform's business.
/// </summary>
public interface ISecretStore
{
    bool Has(string accountId);

    string? Read(string accountId);

    void Write(string accountId, string token);

    void Delete(string accountId);

    /// <summary>Every stored token for the given accounts, skipping any that cannot be read.</summary>
    Dictionary<string, string> ReadAll(IEnumerable<string> accountIds);

    /// <summary>Drops tokens that no longer belong to any configured account.</summary>
    void Prune(IEnumerable<string> keep);

    // ---- The single-token layout from before multi-account support ----------

    bool HasLegacy { get; }

    string? ReadLegacy();

    void DeleteLegacy();
}
