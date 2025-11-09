// Scan/IScanStrategy.cs

using DuplicateFileFinderLib.Indexing;

namespace DuplicateFileFinderLib.Scan;

public interface IScanStrategy
{
    IAsyncEnumerable<FileEntryMeta> EnumerateChildrenAsync(string root, CancellationToken ct);
}