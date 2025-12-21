// DuplicateFileFinderLib/Repository/Storage/RepoStore.Checkpoints.cs

using MemoryPack;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Storage;

internal static partial class RepoStore
{
    private const string CheckpointDirName = "checkpoints";

    private static string GetCheckpointDir(string repoPath)
        => Path.Combine(repoPath, CheckpointDirName);

    private static string GetCheckpointPath(string repoPath, long scanRootId)
        => Path.Combine(GetCheckpointDir(repoPath), $"{scanRootId}.checkpoint.mpk");

    public static async Task SaveScanCheckpointAsync(
        string repoPath,
        ScanCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(GetCheckpointDir(repoPath));
        var path = GetCheckpointPath(repoPath, checkpoint.ScanRootId);

        // MemoryPack serialize
        var bytes = MemoryPackSerializer.Serialize(checkpoint);

        // Durable-ish write: write temp then move.
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);

        // Replace existing.
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<ScanCheckpoint?> TryLoadScanCheckpointAsync(
        string repoPath,
        long scanRootId,
        CancellationToken ct = default)
    {
        var path = GetCheckpointPath(repoPath, scanRootId);
        if (!File.Exists(path))
            return null;

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return MemoryPackSerializer.Deserialize<ScanCheckpoint>(bytes);
    }

    public static Task DeleteScanCheckpointAsync(
        string repoPath,
        long scanRootId,
        CancellationToken ct = default)
    {
        var path = GetCheckpointPath(repoPath, scanRootId);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // tolerate
        }

        return Task.CompletedTask;
    }
}
