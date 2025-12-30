// Repository/Storage/RepoStore.cs

using DuplicateFileFinderLib.Repository.Storage.Models;
using MemoryPack;
using NLog;

namespace DuplicateFileFinderLib.Repository.Storage;

internal static partial class RepoStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private static string NewUniqueTmpPath(string finalPath)
        => $"{finalPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    // ---------------- meta ----------------

    internal static async Task SaveMetaAsync(string repoPath, RepoMetaFile meta, CancellationToken ct = default)
    {
        repoPath = Path.GetFullPath(repoPath);
        Directory.CreateDirectory(repoPath);

        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var metaPath = GetMetaPath(repoPath);
            var tmpPath = NewUniqueTmpPath(metaPath);

            try
            {
                await using (var fs = new FileStream(
                                 tmpPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 4096,
                                 useAsync: true))
                {
                    await MemoryPackSerializer.SerializeAsync(fs, meta, cancellationToken: ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(tmpPath, metaPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            WriteGate.Release();
        }
    }

    internal static async Task<RepoMetaFile?> LoadMetaAsync(string repoPath, CancellationToken ct = default)
    {
        repoPath = Path.GetFullPath(repoPath);

        var metaPath = GetMetaPath(repoPath);
        if (!File.Exists(metaPath))
            return null;

        await using var fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await MemoryPackSerializer.DeserializeAsync<RepoMetaFile>(fs, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    // ---------------- scanroot snapshot v2 ----------------

    internal static async Task<ScanRootSnapshotV2?> LoadScanRootSnapshotV2Async(
        string repoPath,
        long scanRootId,
        CancellationToken ct = default)
    {
        repoPath = Path.GetFullPath(repoPath);

        var path = GetRootSnapshotPath(repoPath, scanRootId);
        if (!File.Exists(path))
            return null;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshotV2 = await MemoryPackSerializer.DeserializeAsync<ScanRootSnapshotV2>(fs, cancellationToken: ct)
            .ConfigureAwait(false);


        return snapshotV2;
    }

    internal static async Task SaveScanRootSnapshotV2Async(
        string repoPath,
        ScanRootSnapshotV2 snapshot,
        CancellationToken ct = default)
    {
        repoPath = Path.GetFullPath(repoPath);

        var rootsFolder = GetRootsFolder(repoPath);
        Directory.CreateDirectory(rootsFolder);

        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = GetRootSnapshotPath(repoPath, snapshot.ScanRootId);
            var tmpPath = NewUniqueTmpPath(path);

            try
            {
                await using (var fs = new FileStream(
                                 tmpPath,
                                 FileMode.CreateNew,
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
            finally
            {
                try
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            WriteGate.Release();
        }
    }

    public static async Task DeleteScanRootSnapshotAsync(string repoPath, long scanRootId, CancellationToken ct)
    {
        repoPath = Path.GetFullPath(repoPath);

        // Delete is also gated to avoid racing a writer.
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = GetRootSnapshotPath(repoPath, scanRootId);

            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    Log.Error($"Delete ScanRoot snapshot failed (ScanRootId: {scanRootId}).", path);
                }
            }
        }
        finally
        {
            WriteGate.Release();
        }

    }

    // ---------------- helpers ----------------

    private const string MetaFileName = "repo.mp";
    private const string RootsFolderName = "roots";

    private static string GetMetaPath(string repoPath)
        => Path.Combine(repoPath, MetaFileName);

    private static string GetRootsFolder(string repoPath)
        => Path.Combine(repoPath, RootsFolderName);

    private static string GetRootSnapshotPath(string repoPath, long scanRootId)
        => Path.Combine(GetRootsFolder(repoPath), $"{scanRootId}.mp");
}