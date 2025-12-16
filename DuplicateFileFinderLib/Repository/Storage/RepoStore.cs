// Repository/Storage/RepoStore.cs

using DuplicateFileFinderLib.Logging;
using MemoryPack;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Storage;

public static class RepoStore
{
    public static async Task SaveMetaAsync(string repoPath, RepoMetaFile meta, CancellationToken ct = default)
    {
        Directory.CreateDirectory(repoPath);
        var metaPath = GetMetaPath(repoPath);

        await using var fs = new FileStream(
            metaPath,
            FileMode.Create,
            FileAccess.Write, 
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        await MemoryPackSerializer.SerializeAsync(fs, meta, cancellationToken: ct).ConfigureAwait(false);
    }

    public static async Task<RepoMetaFile?> LoadMetaAsync(string repoPath, CancellationToken ct = default)
    {
        var metaPath = GetMetaPath(repoPath);
        if (!File.Exists(metaPath))
            return null;

        await using var fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await MemoryPackSerializer.DeserializeAsync<RepoMetaFile>(fs, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    internal static async Task SaveScanRootSnapshotAsync(
        string repoPath,
        ScanRootSnapshot snapshot,
        CancellationToken ct = default)
    {
        var newSnapShot = ConvertToSnapshotV2(snapshot);

        await SaveScanRootSnapshotV2Async(repoPath, newSnapShot, ct);
        return;

        var rootsFolder = GetRootsFolder(repoPath);
        Directory.CreateDirectory(rootsFolder);
        
        var path = GetRootSnapshotPath(repoPath, snapshot.ScanRootId);
        var tmpPath = path + ".tmp";
        
        await using (var fs = new FileStream(
                         tmpPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            await MemoryPackSerializer.SerializeAsync(fs, snapshot, cancellationToken: ct).ConfigureAwait(false);
        
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        
        File.Move(tmpPath, path, overwrite: true);
    }

    private static ScanRootSnapshotV2 ConvertToSnapshotV2(ScanRootSnapshot snapshot)
    {
        // 1. Collect and intern all strings used by this snapshot
        var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<string>();

        int Intern(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return -1;

            if (!stringIndex.TryGetValue(s, out int idx))
            {
                idx = strings.Count;
                stringIndex.Add(s, idx);
                strings.Add(s);
            }

            return idx;
        }

        // 2. Convert directories
        var oldDirs = snapshot.Dirs;
        var newDirs = new DirRecordV2[oldDirs.Length];

        for (int i = 0; i < oldDirs.Length; i++)
        {
            var d = oldDirs[i];

            newDirs[i] = new DirRecordV2
            {
                DirId = d.DirId,
                ParentDirId = d.ParentDirId ?? -1,
                NameStrIdx = Intern(d.Name),
                LastSeenScanSequence = d.LastSeenScanSequence,
                Status = d.Status,
                ErrorMessageStrIdx = Intern(d.ErrorMessage),
                ModifiedTicks = d.Modified?.UtcTicks ?? 0,
                CreatedTicks = d.Created?.UtcTicks ?? 0
            };
        }

        // 3. Convert files
        var oldFiles = snapshot.Files;
        var newFiles = new FileRecordV2[oldFiles.Length];

        for (int i = 0; i < oldFiles.Length; i++)
        {
            var f = oldFiles[i];

            newFiles[i] = new FileRecordV2
            {
                FileId = f.FileId,
                DirId = f.DirId,
                NameStrIdx = Intern(f.Name),
                Size = f.Size,
                Hash = f.Hash,
                ModifiedTicks = f.Modified?.UtcTicks ?? 0,
                CreatedTicks = f.Created?.UtcTicks ?? 0,
                LastSeenScanSequence = f.LastSeenScanSequence,
                Status = f.Status,
                ErrorMessageStrIdx = Intern(f.ErrorMessage)
            };
        }

        // 4. Build the string pool
        var pool = PackedStringPool.FromStrings(strings.ToArray());

        // 5. Produce snapshot V2
        return new ScanRootSnapshotV2
        {
            ScanRootId = snapshot.ScanRootId,
            StringPool = pool,
            Dirs = newDirs,
            Files = newFiles
        };
    }
    
    private static ScanRootSnapshot ConvertFromSnapshotV2(ScanRootSnapshotV2 snapshot)
    {
        string? DecodeNullable(int idx)
            => idx >= 0 ? snapshot.StringPool.GetString(idx) : null;

        string DecodeNonNull(int idx)
            => idx >= 0 ? snapshot.StringPool.GetString(idx) : string.Empty;

        var dirsV2 = snapshot.Dirs;
        var dirs = new DirRecord[dirsV2.Length];

        for (int i = 0; i < dirsV2.Length; i++)
        {
            var d = dirsV2[i];

            dirs[i] = new DirRecord
            {
                DirId = d.DirId,
                ParentDirId = d.ParentDirId >= 0 ? d.ParentDirId : null,
                Name = DecodeNonNull(d.NameStrIdx),
                LastSeenScanSequence = d.LastSeenScanSequence,
                Status = d.Status,
                ErrorMessage = DecodeNullable(d.ErrorMessageStrIdx),
                Modified = d.ModifiedTicks != 0
                    ? new DateTimeOffset(d.ModifiedTicks, TimeSpan.Zero)
                    : null,
                Created = d.CreatedTicks != 0
                    ? new DateTimeOffset(d.CreatedTicks, TimeSpan.Zero)
                    : null
            };
        }

        var filesV2 = snapshot.Files;
        var files = new FileRecord[filesV2.Length];

        for (int i = 0; i < filesV2.Length; i++)
        {
            var f = filesV2[i];

            files[i] = new FileRecord
            {
                FileId = f.FileId,
                DirId = f.DirId,
                Name = DecodeNonNull(f.NameStrIdx),
                Size = f.Size,
                Hash = f.Hash,
                Modified = f.ModifiedTicks != 0
                    ? new DateTimeOffset(f.ModifiedTicks, TimeSpan.Zero)
                    : null,
                Created = f.CreatedTicks != 0
                    ? new DateTimeOffset(f.CreatedTicks, TimeSpan.Zero)
                    : null,
                LastSeenScanSequence = f.LastSeenScanSequence,
                Status = f.Status,
                ErrorMessage = DecodeNullable(f.ErrorMessageStrIdx)
            };
        }

        return new ScanRootSnapshot
        {
            ScanRootId = snapshot.ScanRootId,
            Dirs = dirs,
            Files = files
        };
    }

    internal static async Task<(ScanRootSnapshot? oldSnapshot, ScanRootSnapshotV2? newSnapshot)> LoadScanRootSnapshotAsync(
        string repoPath,
        long scanRootId,
        CancellationToken ct = default)
    {
        var snapshotV2 = await LoadScanRootSnapshotV2Async(repoPath, scanRootId, ct);
        
        ScanRootSnapshot? snapshot = null;
        using (TimingLog.Start("Convert to old format"))
        {
            if (snapshotV2.HasValue)
                snapshot = ConvertFromSnapshotV2(snapshotV2.Value);
        }
        
        return (snapshot, snapshotV2);
    }
    
    internal static async Task<ScanRootSnapshotV2?> LoadScanRootSnapshotV2Async(
        string repoPath,
        long scanRootId,
        CancellationToken ct = default)
    {
        ScanRootSnapshotV2 snapshotV2;
        using (TimingLog.Start("New Deserialize"))
        {

            var path = GetRootSnapshotPath(repoPath, scanRootId);
            if (!File.Exists(path))
                return null;


            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            snapshotV2 = await MemoryPackSerializer.DeserializeAsync<ScanRootSnapshotV2>(fs, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        
        return snapshotV2;
    }
    
    internal static async Task SaveScanRootSnapshotV2Async(
        string repoPath,
        ScanRootSnapshotV2 snapshot,
        CancellationToken ct = default)
    {
        var rootsFolder = GetRootsFolder(repoPath);
        Directory.CreateDirectory(rootsFolder);

        var path = GetRootSnapshotPath(repoPath, snapshot.ScanRootId);
        var tmpPath = path + ".tmp";

        await using (var fs = new FileStream(
                         tmpPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            await MemoryPackSerializer.SerializeAsync(fs, snapshot, cancellationToken: ct).ConfigureAwait(false);

            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        
        File.Move(tmpPath, path, overwrite: true);
    }

    // Helpers

    private const string MetaFileName = "repo.mp";
    private const string RootsFolderName = "roots";

    private static string GetMetaPath(string repoPath)
        => Path.Combine(repoPath, MetaFileName);

    private static string GetRootsFolder(string repoPath)
        => Path.Combine(repoPath, RootsFolderName);

    private static string GetRootSnapshotPath(string repoPath, long scanRootId)
        => Path.Combine(GetRootsFolder(repoPath), $"{scanRootId}.mp");
}