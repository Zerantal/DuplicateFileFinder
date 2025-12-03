namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    /// <summary>
    /// Ensures the repo is migrated up to RepoSchemaVersion.
    /// Applies stepwise migrations and persists changes (snapshot + meta + scanroots/scanruns)
    /// if any migration actually ran.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    private void MigrateToLatest()
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
                    SyncMetaFile_NoLock();
                    _ = PersistMetaAsync();
                }
                return;
            }

            // After migration(s), write a fresh snapshot + meta + scanroots/scanruns.
            // SaveScanSnapshots_NoLock will include the migrated _meta (with new SchemaVersion).

            SyncMetaFile_NoLock();
            _ = PersistMetaAsync();
            
            SaveScanSnapshots_NoLock();
        }
    }
}
