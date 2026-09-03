namespace GitAlert.Configuration;

/// <summary>
/// Brings a settings file written before multi-account support up to date: the single token
/// becomes one account, and every repository is attached to it. Runs silently at startup, so an
/// existing install keeps working without the user touching anything.
/// </summary>
public static class SettingsMigration
{
    /// <summary>Returns true when something was changed and the settings should be saved.</summary>
    public static bool Apply(AppSettings settings, SecureTokenStore tokens)
    {
        var changed = false;

        // Repositories from the old layout carry no account id.
        var orphans = settings.Repositories.Where(r => string.IsNullOrEmpty(r.AccountId)).ToList();
        var legacyToken = tokens.ReadLegacy();
        var hasLegacyState = legacyToken is not null || orphans.Count > 0;

        if (settings.Accounts.Count == 0 && hasLegacyState)
        {
            // The login is unknown until the token is used; MonitorService fills it in after the
            // first successful call and the settings are saved again then.
            var account = GitHubAccount.Create(login: string.Empty);
            account.IncludeInbox = settings.IncludeInbox ?? true;

            settings.Accounts.Add(account);

            foreach (var repository in orphans)
            {
                repository.AccountId = account.Id;
            }

            if (legacyToken is not null)
            {
                tokens.Write(account.Id, legacyToken);
                tokens.DeleteLegacy();
            }

            changed = true;
        }
        else if (orphans.Count > 0 && settings.Accounts.Count > 0)
        {
            // Half-migrated file, or a repository added by hand: adopt them into the first account.
            var accountId = settings.Accounts[0].Id;

            foreach (var repository in orphans)
            {
                repository.AccountId = accountId;
            }

            changed = true;
        }

        if (settings.IncludeInbox is not null)
        {
            settings.IncludeInbox = null;
            changed = true;
        }

        // A leftover legacy blob with no account to attach it to is dead weight.
        if (settings.Accounts.Count > 0 && tokens.HasLegacy && legacyToken is null)
        {
            tokens.DeleteLegacy();
            changed = true;
        }

        return changed;
    }
}
