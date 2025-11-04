// DuplicateFileFinderLib/Hashing/GroupingAdapter.cs

using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLib.Grouping;

public interface IGroupingService
{
    Task AssignAsync(FolderNode scope, CancellationToken ct);
    void Reset();
}

public sealed class FileSystemGroupsAdapter : IGroupingService
{
    private FileSystemGroups _impl = new();

    public Task AssignAsync(FolderNode scope, CancellationToken ct)
    {
        return _impl.AssignGroups(scope);
    }

    public void Reset()
    {
        _impl = new FileSystemGroups();
    }
}