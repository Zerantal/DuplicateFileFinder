namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public interface IDuplicateFileDeletionService
{
    Task<DuplicateFileDeletionResult> DeleteAsync(long fileId, string fullPath, CancellationToken ct = default);
}

public sealed record DuplicateFileDeletionResult(
    bool Success,
    DuplicateFileDeletionFailure? Failure = null);

public enum DuplicateFileDeletionFailure
{
    CancelledByUser,
    FullPathBlank,
    FileSystemDeleteFailed,
    HandleResolutionFailed,
    RepoDeleteFailed
}
