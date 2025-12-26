namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    /// <summary>
    /// Ensures the repo is migrated up to RepoSchemaVersion.
    /// Applies stepwise migrations and persists changes (snapshot + meta + scanroots/scanruns)
    /// if any migration actually ran.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private async Task MigrateToLatest()
    {
        bool migrated = false;

        lock (_sync)
        {
            while (Meta.SchemaVersion < RepoSchemaVersion)
            {
                switch (Meta.SchemaVersion)
                {
                    default:
                        throw new InvalidOperationException(
                            $"Unknown repo schema version: {Meta.SchemaVersion}. " +
                            $"Cannot migrate to {RepoSchemaVersion}.");
                }
            }
            
            // If nothing changed, ensure meta schema is at least RepoSchemaVersion and leave.
            if (!migrated)
            {
                if (Meta.SchemaVersion != RepoSchemaVersion)
                {
                    Meta = Meta with { SchemaVersion = RepoSchemaVersion };
                    MarkMetaDirty_NoLock();
                }
            }
        }

        await PersistMetaIfDirtyAsync().ConfigureAwait(false);
    }
}
