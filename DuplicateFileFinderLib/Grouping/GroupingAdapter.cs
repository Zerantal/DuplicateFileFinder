// DuplicateFileFinderLib/Hashing/GroupingAdapter.cs

using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLib.Grouping;

public interface IGroupingService
{
    Task AssignGroupsAsync(FolderNode scope, Action<long>? onProgress = null, CancellationToken ct = default);
    void Reset();
}

public sealed class FileSystemGroupsAdapter : IGroupingService
{
    private FileSystemGroups _impl = new();

    public Task AssignGroupsAsync(FolderNode scope, Action<long>? onProgress, CancellationToken ct)
    {
        return _impl.AssignGroups(scope, onProgress, ct);
    }

    public void Reset()
    {
        _impl = new FileSystemGroups();
    }
}