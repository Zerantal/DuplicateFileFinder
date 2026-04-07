namespace DuplicateFileFinder.Gui.Application.Deletion;

public sealed record DeleteItemResult(
    bool Success,
    DeleteItemFailure? Failure = null);

public enum DeleteItemFailure
{
    FullPathBlank,
    InvalidHandle,
    CancelledByUser,
    FileSystemDeleteFailed,
    RepoDeleteFailed,
    Timeout
}
