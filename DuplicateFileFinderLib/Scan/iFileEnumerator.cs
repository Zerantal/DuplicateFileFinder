
namespace DuplicateFileFinderLib.Scan;

public interface IFileEnumerator
{
    IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token);
}