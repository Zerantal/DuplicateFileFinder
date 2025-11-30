// Repository/Storage/RepoStore.cs

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

    public static async Task SaveScanRootSnapshotAsync(
        string repoPath,
        ScanRootSnapshotOnDisk snapshot,
        CancellationToken ct = default)
    {
        var rootsFolder = GetRootsFolder(repoPath);
        Directory.CreateDirectory(rootsFolder);

        var path = GetRootSnapshotPath(repoPath, snapshot.ScanRootId);

        await using var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            bufferSize: 8192,
            useAsync: true);
        
        await MemoryPackSerializer.SerializeAsync(fs, snapshot, cancellationToken: ct).ConfigureAwait(false);
        
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<ScanRootSnapshotOnDisk?> LoadScanRootSnapshotAsync(
        string repoPath,
        Guid scanRootId,
        CancellationToken ct = default)
    {
        var path = GetRootSnapshotPath(repoPath, scanRootId);
        if (!File.Exists(path))
            return null;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await MemoryPackSerializer.DeserializeAsync<ScanRootSnapshotOnDisk>(fs, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    // Helpers

    private const string MetaFileName = "repo.mp";
    private const string RootsFolderName = "roots";

    private static string GetMetaPath(string repoPath)
        => Path.Combine(repoPath, MetaFileName);

    private static string GetRootsFolder(string repoPath)
        => Path.Combine(repoPath, RootsFolderName);

    private static string GetRootSnapshotPath(string repoPath, Guid scanRootId)
        => Path.Combine(GetRootsFolder(repoPath), $"{scanRootId:N}.mp");
}