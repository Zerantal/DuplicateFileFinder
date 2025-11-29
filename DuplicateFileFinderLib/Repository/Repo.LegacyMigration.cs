// DuplicateFileFinderLib/Repository/Repo.LegacyMigration.cs

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using NLog;

namespace DuplicateFileFinderLib.Repository;

internal static class RepoMigration
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// One-shot migration from the legacy "monolithic snapshot" layout
    /// (meta.json + snapshot.bin + scanroots.json + scanruns.json)
    /// to the new RepoStore layout (RepoMetaFile + per-scan-root snapshots).
    /// 
    /// Safe to call repeatedly; it will no-op if the new format is already present
    /// or if there is nothing legacy to migrate.
    /// </summary>
    public static async Task TryMigrateLegacySnapshotAsync(string repoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentNullException(nameof(repoPath));

        repoPath = Path.GetFullPath(repoPath);

        // If the new store already has meta, nothing to do.
        // (This also covers the case where we've already migrated once.)
        var existingMeta = await RepoStore.LoadMetaAsync(repoPath, ct).ConfigureAwait(false);
        if (existingMeta is not null)
        {
            Log.Debug("RepoMigration: RepoStore meta already present at '{0}', skipping legacy migration.", repoPath);
            return;
        }

        // Legacy files we expect for the old layout.
        var metaJsonPath    = Path.Combine(repoPath, "meta.json");
        var snapshotBinPath = Path.Combine(repoPath, "snapshot.bin");

        // If there is no legacy meta/snapshot pair, nothing to migrate.
        if (!File.Exists(metaJsonPath) || !File.Exists(snapshotBinPath))
        {
            Log.Debug("RepoMigration: No legacy meta.json/snapshot.bin in '{0}', nothing to migrate.", repoPath);
            return;
        }

        Log.Info("RepoMigration: Migrating legacy repo at '{0}' to new RepoStore layout.", repoPath);

        // 1. Open the legacy repo using the old path.
        //    This will:
        //      - Load meta.json + snapshot.bin
        //      - Replay .delta logs
        //      - Load scanruns.json / scanroots.json
        //      - Run schema migrations up to RepoSchemaVersion (5)
        //
        //    We then treat that in-memory state as the authoritative view
        //    and re-emit it in the new on-disk layout.
        await using var legacyRepo = Repo.Open(repoPath);

        // 2. Build the RepoMetaFile for the new store.
        var meta   = legacyRepo.Meta;                // internal property
        var roots  = legacyRepo.ScanRootsView.ToList();
        var runs   = legacyRepo.ScanRunsView.ToList();

        var metaFile = new RepoMetaFile
        {
            Meta      = meta,
            ScanRoots = roots,
            ScanRuns  = runs
        };

        ct.ThrowIfCancellationRequested();

        await RepoStore.SaveMetaAsync(repoPath, metaFile, ct).ConfigureAwait(false);

        // 3. Build per-scan-root snapshots from the live snapshot view.
        //    We use GetSnapshot() to get independent copies of the dictionaries.
        var view   = legacyRepo.GetSnapshot();
        var allDirs  = view.Dirs;
        var allFiles = view.Files;

        // Pre-group files by DirId to avoid repeated scans per root.
        var filesByDir = allFiles.Values
            .GroupBy(f => f.DirId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            // If the ScanRoot doesn't have a concrete DirId yet (e.g. very old data),
            // there is nothing we can meaningfully snapshot for it.
            if (root.DirId == Guid.Empty)
            {
                Log.Debug("RepoMigration: ScanRoot {0} has DirId=Guid.Empty, skipping snapshot.", root.Id);
                continue;
            }

            if (!allDirs.ContainsKey(root.DirId))
            {
                Log.Debug("RepoMigration: ScanRoot {0} DirId {1} not found in Dirs, skipping snapshot.",
                    root.Id, root.DirId);
                continue;
            }

            var dirIds = CollectDirSubtree(root.DirId, allDirs);
            if (dirIds.Count == 0)
            {
                Log.Debug("RepoMigration: ScanRoot {0} subtree is empty, skipping snapshot.", root.Id);
                continue;
            }

            var dirRecords = dirIds.Select(id => allDirs[id]).ToArray();

            var fileList = new List<FileRecord>();
            foreach (var dirId in dirIds)
            {
                if (filesByDir.TryGetValue(dirId, out var filesInDir))
                    fileList.AddRange(filesInDir);
            }

            var rootSnap = new ScanRootSnapshotOnDisk
            {
                ScanRootId = root.Id,
                Dirs       = dirRecords,
                Files      = fileList.ToArray()
            };

            await RepoStore.SaveScanRootSnapshotAsync(repoPath, rootSnap, ct).ConfigureAwait(false);

            Log.Debug("RepoMigration: Wrote per-root snapshot for ScanRoot {0} (Dirs={1}, Files={2}).",
                root.Id, dirRecords.Length, fileList.Count);
        }

        // 4. Optionally, leave the legacy files in place as a fallback.
        //    If you want, you can rename them to *.legacy.* here, but it's safer
        //    to keep them until you've verified migration in your own tooling.

        Log.Info("RepoMigration: Migration of legacy repo at '{0}' completed.", repoPath);
    }

    /// <summary>
    /// Collects the set of directory IDs in the subtree rooted at <paramref name="rootDirId"/>.
    /// This mirrors the BFS implementation used in Repo.Persistence for per-root snapshots.
    /// </summary>
    // private static HashSet<Guid> CollectDirSubtree(
    //     Guid rootDirId,
    //     IReadOnlyDictionary<Guid, DirRecord> allDirs)
    // {
    //     var result = new HashSet<Guid>();
    //     if (!allDirs.ContainsKey(rootDirId))
    //         return result;
    //
    //     result.Add(rootDirId);
    //
    //     var queue = new Queue<Guid>();
    //     queue.Enqueue(rootDirId);
    //
    //     while (queue.Count > 0)
    //     {
    //         var current = queue.Dequeue();
    //
    //         foreach (var dir in allDirs.Values)
    //         {
    //             if (dir.ParentId is Guid parentId &&
    //                 parentId == current &&
    //                 result.Add(dir.Id))
    //             {
    //                 queue.Enqueue(dir.Id);
    //             }
    //         }
    //     }
    //
    //     return result;
    // }
    private static HashSet<Guid> CollectDirSubtree(
        Guid rootDirId,
        IReadOnlyDictionary<Guid, DirRecord> allDirs)
    {
        var result = new HashSet<Guid>();

        // Root not present? Nothing to do.
        if (!allDirs.ContainsKey(rootDirId))
            return result;

        // Build parent -> children index once for this call.
        // This is O(N) over allDirs and avoids N * N scanning.
        var childrenByParent = new Dictionary<Guid, List<Guid>>(allDirs.Count);

        foreach (var dir in allDirs.Values)
        {
            if (dir.ParentId is Guid parentId)
            {
                if (!childrenByParent.TryGetValue(parentId, out var list))
                {
                    list = new List<Guid>();
                    childrenByParent[parentId] = list;
                }

                list.Add(dir.Id);
            }
        }

        var queue = new Queue<Guid>();
        result.Add(rootDirId);
        queue.Enqueue(rootDirId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!childrenByParent.TryGetValue(current, out var children))
                continue;

            for (int i = 0; i < children.Count; i++)
            {
                var childId = children[i];
                if (result.Add(childId))
                    queue.Enqueue(childId);
            }
        }

        return result;
    }

}
