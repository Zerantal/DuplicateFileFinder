
namespace DuplicateFileFinderLib.Scan;

public interface IFileEnumerator
{
    IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token);    // Legacy
    IAsyncEnumerable<FsEntry> EnumerateChildrenAsync(string dir, CancellationToken token);
}