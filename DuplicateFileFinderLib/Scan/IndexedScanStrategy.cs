using DuplicateFileFinderLib.Indexing;

namespace DuplicateFileFinderLib.Scan;

public sealed class IndexedScanStrategy : IScanStrategy
{
    private readonly IMetadataIndex _index;
    // private readonly IDirectoryEnumerator _reader;

    // public async IAsyncEnumerable<FileEntryMeta> EnumerateAsync(string root, [EnumeratorCancellation] CancellationToken ct)
    // {
    //     // 1) Initial sync pass: walk dirs, per-dir Diff → upserts in batches
    //     await foreach (var dir in _reader.EnumerateDirectories(root, ct))
    //     {
    //         var diff = await _index.DiffDirectoryAsync(dir.Path, dir.MTimeUtc, ct);
    //         if (diff is { ToInsert.Count: 0, ToUpdate.Count: 0, ToDelete.Count: 0 })
    //             continue;
    //
    //         await _index.BeginDirectoryBatchAsync(dir.Path, dir.MTimeUtc, diff.ToInsert.Count + diff.ToUpdate.Count, ct);
    //         foreach (var f in diff.ToInsert.Concat(diff.ToUpdate))
    //             await _index.UpsertFileAsync(f, ct);
    //         await _index.EndDirectoryBatchAsync(ct);
    //     }
    //
    //     // 2) Enumeration for the rest of the pipeline comes from the index
    //     await foreach (var meta in _index.EnumerateAllAsync(ct))
    //         yield return meta;
    // }
    // public IAsyncEnumerable<FileEntryMeta> EnumerateAsync(string root, CancellationToken ct)
    // {
    //     throw new NotImplementedException();
    // }
    public IAsyncEnumerable<FileEntryMeta> EnumerateAsync(string root, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}