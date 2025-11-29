// Repository/Storage/RepoMigration.cs
using System.IO;
using MemoryPack;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Storage;

public static class RepoMigration
{
    private const string LegacySnapshotFileName = "snapshot.mp";

    public static async Task<bool> TryMigrateLegacySnapshotAsync(string repoPath, CancellationToken ct = default)
    {
        var legacyPath = Path.Combine(repoPath, LegacySnapshotFileName);
        if (!File.Exists(legacyPath))
            return false;

        // 1. Load old snapshot
        RepoSnapshot? legacy;
        await using (var fs = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            legacy = await MemoryPackSerializer.DeserializeAsync<RepoSnapshot>(fs, cancellationToken: ct)
                .ConfigureAwait(false);
        }

        if (legacy is null)
            throw new InvalidOperationException("Failed to deserialize legacy RepoSnapshot.");

        // 2. Write new meta
        var metaFile = new RepoMetaFile
        {
            Meta      = legacy.Meta,
            ScanRoots = legacy.ScanRoots,
            ScanRuns  = legacy.ScanRuns
        };

        await RepoStore.SaveMetaAsync(repoPath, metaFile, ct).ConfigureAwait(false);

        // 3. Build per-root snapshots

        // Build quick lookup dictionaries
        var filesByDir = legacy.Files.Values.GroupBy(f => f.DirId)
                                            .ToDictionary(g => g.Key, g => g.ToArray());
        var dirsById   = legacy.Dirs; // already Dictionary<Guid, DirRecord>

        foreach (var scanRoot in legacy.ScanRoots)
        {
            if (!dirsById.ContainsKey(scanRoot.DirId))
                continue; // or throw if this should never happen

            // Collect all DirIds under this root (simple BFS over ParentId)
            var dirIds = CollectDirSubtree(scanRoot.DirId, dirsById);

            var dirRecords = dirIds.Select(id => dirsById[id]).ToArray();

            var fileList = new List<FileRecord>();
            foreach (var dirId in dirIds)
            {
                if (filesByDir.TryGetValue(dirId, out var filesInDir))
                    fileList.AddRange(filesInDir);
            }

            var rootSnap = new ScanRootSnapshotOnDisk
            {
                ScanRootId = scanRoot.Id,
                Dirs  = dirRecords,
                Files = fileList.ToArray()
            };

            await RepoStore.SaveScanRootSnapshotAsync(repoPath, rootSnap, ct).ConfigureAwait(false);
        }

        // 4. Optionally archive or delete old snapshot
        var backupPath = Path.Combine(repoPath, LegacySnapshotFileName + ".bak");
        File.Move(legacyPath, backupPath, overwrite: true);

        return true;
    }

    private static HashSet<Guid> CollectDirSubtree(Guid rootDirId, Dictionary<Guid, DirRecord> allDirs)
    {
        var result = new HashSet<Guid> { rootDirId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootDirId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var kvp in allDirs)
            {
                var dir = kvp.Value;
                if (dir.ParentId is Guid parentId && parentId == current && result.Add(dir.Id))
                    queue.Enqueue(dir.Id);
            }
        }

        return result;
    }
}
