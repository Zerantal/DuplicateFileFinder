// Scan/IEntryEnumerator.cs

using DuplicateFileFinderLib.Indexing;

namespace DuplicateFileFinderLib.Scan;

public interface IEntryEnumerator
{
    // Enumerates immediate children only for a given directory
    IAsyncEnumerable<FileEntryMeta> EnumerateChildrenAsync(string directoryPath, CancellationToken ct);
}