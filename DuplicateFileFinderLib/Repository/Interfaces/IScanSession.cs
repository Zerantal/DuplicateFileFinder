// DuplicateFileFinderLib/Repository/Interfaces/IScanSession.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IScanSession : IAsyncDisposable
{
    DirCursor RootDirCursor { get; }

    void SetPendingDirsProvider(Func<PendingDir[]> getPendingDirs);

    DirEnumerationContext BeginDirectory(DirCursor parentDirId);

    DirCursor OnDirectoryFound(in ObservedDir dir, ref DirEnumerationContext ctx);

    FileHashDecision OnFileFound(in ObservedFile file, ref DirEnumerationContext ctx);

    void OnFileHashCompleted(in FileHashToken token, ReadOnlyMemory<byte> hashBytes, string? errorMessage = null);

    void EndDirectory(ref DirEnumerationContext ctx);

    Task<ScanCompletionInfo> CompleteAsync(CancellationToken cancellationToken = default);

    Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default);
}
