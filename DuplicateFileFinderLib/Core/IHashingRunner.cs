namespace DuplicateFileFinderLib.Core;

internal readonly record struct FileToHash<T>(string FullPath, T Token);

internal interface IHashingRunner<T>
{
    Task HashFilesAsync(
        List<FileToHash<T>> filesToHash,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        Action<T, ReadOnlyMemory<byte>, string?> onFileHashed,
        CancellationToken ct);

    int ReadBufferSize { get; set; }
    int MaxDegreeOfParallelism { get; set; }
}
