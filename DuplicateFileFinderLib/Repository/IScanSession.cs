// DuplicateFileFinderLib/Repository/IScanSession.cs

using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public interface IScanSession : IAsyncDisposable
{
    ScanRun Run { get; }
    long RunId { get; }
    string RootPath { get; }

    /// <summary>
    /// Ensure that the directory at <paramref name="fullPath"/> has a stable DirRecord.FileId
    /// for this scan session. Creates any missing parents as dummy dirs (Status=None),
    /// and the leaf with the requested <paramref name="status"/> (or a default if null).
    /// Returns the directory FileId.
    /// </summary>
    long AddOrUpdateDirectory(
        string fullPath,
        ScanEntryStatus? status       = null,
        string?         errorMessage = null);

    /// <summary>
    /// Ensure that the file at <paramref name="fullFilePath"/> has a stable FileRecord.FileId
    /// for this scan session. Creates the parent directory (and ancestors) as needed,
    /// and records the latest state for this file path.
    ///
    /// For new files:
    /// - size: defaults to 0 if null
    /// - hash: defaults to default(HashKey) if null (e.g. "not computed")
    /// - modified/created: default(DateTimeOffset) if null
    /// - status: defaults to ScanEntryStatus.Enumerated if null
    ///
    /// For existing files:
    /// - null arguments mean "keep the existing value".
    /// </summary>
    void AddOrUpdateFile(
        string           fullFilePath,
        long?             size         = null,
        HashKey?          hash         = null,
        DateTimeOffset?   modified     = null,
        DateTimeOffset?   created      = null,
        ScanEntryStatus?  status       = null,
        string?          errorMessage = null);

    /// <summary>
    /// Mark an existing directory as deleted in this scan.
    /// The implementation is responsible for emitting appropriate tombstones
    /// in its RepoDelta.
    /// </summary>
    void MarkDirectoryDeleted(long dirId);

    /// <summary>
    /// Mark an existing file as deleted in this scan.
    /// The implementation is responsible for emitting appropriate tombstones
    /// in its RepoDelta.
    /// </summary>
    void MarkFileDeleted(long fileId);

    Task FlushProgressAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
    Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default);
}
