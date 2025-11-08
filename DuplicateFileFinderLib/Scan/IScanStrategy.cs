using DuplicateFileFinderLib.Indexing;

namespace DuplicateFileFinderLib.Scan;

public interface IScanStrategy
{
    IAsyncEnumerable<FileEntryMeta> EnumerateAsync(string root, CancellationToken ct);
}