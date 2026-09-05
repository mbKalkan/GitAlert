namespace GitAlert.Platform;

/// <summary>
/// The account ids whose tokens were handed to the platform's secret store. The keychain and the
/// secret service can look a token up by id but cannot be asked "which of these are GitAlert's"
/// cheaply, so pruning the tokens of deleted accounts needs a list of what was ever written. It
/// holds ids only - never a token.
/// </summary>
internal sealed class SecretIndex
{
    private readonly string _path;

    public SecretIndex(string path) => _path = path;

    public IReadOnlyList<string> All
    {
        get
        {
            try
            {
                return File.Exists(_path)
                    ? File.ReadAllLines(_path).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal).ToList()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public void Add(string accountId) => Save([.. All, accountId]);

    public void Remove(string accountId) => Save(All.Where(id => !string.Equals(id, accountId, StringComparison.Ordinal)));

    private void Save(IEnumerable<string> ids)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(_path, ids.Distinct(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index is a convenience for pruning; a token that outlives its account is inert.
        }
    }
}
