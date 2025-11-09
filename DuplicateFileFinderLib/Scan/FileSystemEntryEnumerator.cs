// // Scan/FileSystemEntryEnumerator.cs  (adapter over your existing FileSystemEnumerator)
//
// using DuplicateFileFinderLib.Indexing;
// using DuplicateFileFinderLib.Util;
//
// namespace DuplicateFileFinderLib.Scan;
//
// public sealed class FileSystemEntryEnumerator(FileEnumerator fs) : IEntryEnumerator
// {
//     // your existing type
//
//     public async IAsyncEnumerable<FileEntryMeta> EnumerateChildrenAsync(string directoryPath, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
//     {
//         await foreach (var e in fs.EnumerateChildren(directoryPath, ct).ToAsyncEnumerable().WithCancellation(ct))
//         {
//             yield return new FileEntryMeta(
//                 DirPath: Path.GetDirectoryName(e.FullPath)!,
//                 Name: Path.GetFileName(e.FullPath),
//                 SizeBytes: e.Length,
//                 MTimeUtc: e.LastWriteTimeUtc,
//                 CTimeUtc: e.CreationTimeUtc,
//                 Inode: null,
//                 Mode: 0
//             );
//         }
//     }
// }