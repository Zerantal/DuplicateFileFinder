namespace DuplicateFileFinderLib.Indexing;

public interface IMetadataIndex : IAsyncDisposable
{
    VolumeId Volume { get; }
    Task OpenOrCreateAsync(string dbPath, CancellationToken ct);
    Task<IndexStats> GetStatsAsync(CancellationToken ct);

    // Write path
    Task BeginDirectoryBatchAsync(string dirPath, DateTimeOffset dirMtime, long? entryCountHint, CancellationToken ct);
    ValueTask UpsertFileAsync(FileEntryMeta meta, CancellationToken ct);
    Task EndDirectoryBatchAsync(CancellationToken ct);

    // Read path for fast “enumeration”
    IAsyncEnumerable<FileEntryMeta> EnumerateAllAsync(CancellationToken ct); // volume-relative
    Task<DirectoryDiff> DiffDirectoryAsync(string dirPath, DateTimeOffset dirMtime, CancellationToken ct);
}