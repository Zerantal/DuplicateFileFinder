// DuplicateFileFinderLib/Repository/Storage/RepoStore.Checkpoints.cs

using System.Globalization;

using DuplicateFileFinderLib.Repository.Storage.Models;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage;

internal static partial class RepoStore
{
    private const string CheckpointDirName = "checkpoints";

    private static string GetCheckpointDir(string repoPath)
        => Path.Combine(repoPath, CheckpointDirName);

    private static string GetCheckpointPrefix(long scanRootId)
        => $"{scanRootId}.";

    // File name format:
    //   {scanRootId}.{createdAtUtcTicks:D19}.{scanSequence}.checkpoint.mpk
    private static string FormatCheckpointFileName(ScanCheckpoint checkpoint)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{checkpoint.ScanRootId}.{checkpoint.CreatedAtUtcTicks:D19}.{checkpoint.ScanSequence}.checkpoint.mpk");

    public static bool HasScanCheckpoint(string repoPath, long scanRootId)
    {
        var dir = GetCheckpointDir(repoPath);
        if (!Directory.Exists(dir))
            return false;

        var prefix = GetCheckpointPrefix(scanRootId);
        return Directory.EnumerateFiles(dir, $"{scanRootId}.*.checkpoint.mpk").Any(p =>
            Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal));
    }

    public static async Task SaveScanCheckpointAsync(
        string repoPath,
        ScanCheckpoint checkpoint,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(GetCheckpointDir(repoPath));

        // Never overwrite: append-only file per checkpoint
        var fileName = FormatCheckpointFileName(checkpoint);
        var path = Path.Combine(GetCheckpointDir(repoPath), fileName);

        var tmp = NewUniqueTmpPath(path);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await using (var fs = new FileStream(
                                 tmp,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 81920,
                                 useAsync: true))
                {
                    await MemoryPackSerializer.SerializeAsync(fs, checkpoint, cancellationToken: ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }

                // append-only intent, but overwrite is safe if something left an old file
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch { /* best-effort */ }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static async Task<ScanCheckpoint[]> LoadScanCheckpointsAsync(
        string repoPath,
        long scanRootId,
        CancellationToken ct = default)
    {
        var dir = GetCheckpointDir(repoPath);
        if (!Directory.Exists(dir))
            return Array.Empty<ScanCheckpoint>();

        // Load all matching files; sort by CreatedAtUtcTicks (embedded as D19)
        var paths = Directory.EnumerateFiles(dir, $"{scanRootId}.*.checkpoint.mpk")
            .OrderBy(p => p, StringComparer.Ordinal) // lexical order matches ticks due to D19
            .ToArray();

        if (paths.Length == 0)
            return Array.Empty<ScanCheckpoint>();

        var result = new List<ScanCheckpoint>(paths.Length);

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var cp = MemoryPackSerializer.Deserialize<ScanCheckpoint>(bytes);
                if (cp is not null && cp.ScanRootId == scanRootId)
                    result.Add(cp);
            }
            catch
            {
                // tolerate: skip corrupt/partial files
            }
        }

        // Defensive: ensure ordered by tick even if filenames were odd
        return result.OrderBy(c => c.CreatedAtUtcTicks).ToArray();
    }

    public static async Task DeleteScanCheckpointAsync(
        string repoPath,
        long scanRootId,
        CancellationToken ct = default)
    {
        var dir = GetCheckpointDir(repoPath);
        if (!Directory.Exists(dir))
            return;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, $"{scanRootId}.*.checkpoint.mpk"))
                {
                    try
                    { File.Delete(path); }
                    catch { /* tolerate */ }
                }
            }
            catch
            {
                // tolerate
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
