// DuplicateFileFinderLib/Repository/IScanSession.cs

using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IScanSession : IAsyncDisposable
{
    long ScanSequence { get; }

    DirRecord RootDir { get; init; }

    long AddOrUpdateDirectory(DirRecord dir);
    void AddOrUpdateFile(ref FileRecord file);

    Task FlushProgressAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
    Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default);
}
