using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public interface IScanSession
{
    ScanRun Run { get; }
    long ScanSequence { get; }
    string RootPath { get; }
    ValueTask DisposeAsync();

    // void ObserveDir(
    //     Guid id,
    //     Guid? parentId,
    //     string name,
    //     ScanEntryStatus status,
    //     string? errorMessage = null);

    public Guid ObserveDirectory(
        string fullPath,
        ScanEntryStatus status,
        string? errorMessage = null);
    
    // void ObserveFile(
    //     Guid id,
    //     Guid dirId,
    //     string name,
    //     long size,
    //     HashKey hash,
    //     DateTimeOffset modified,
    //     DateTimeOffset created,
    //     ScanEntryStatus status,
    //     string? errorMessage = null);

    public void ObserveFile(
        string fullFilePath,
        long size,
        HashKey hash,
        DateTimeOffset modified,
        DateTimeOffset created,
        ScanEntryStatus status,
        string? errorMessage = null);

    Task FlushProgressAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
    Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default);
}