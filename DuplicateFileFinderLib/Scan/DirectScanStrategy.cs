// Scan/DirectScanStrategy.cs

using DuplicateFileFinderLib.Indexing;

namespace DuplicateFileFinderLib.Scan;

public sealed class DirectScanStrategy : IScanStrategy
{
    private readonly IEntryEnumerator _entries;
    public DirectScanStrategy(IEntryEnumerator entries) => _entries = entries;

    public async IAsyncEnumerable<FileEntryMeta> EnumerateTreeAsync(string root, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Directory recursion stays in FolderNode.TraverseFolders, so we enumerate per directory.
        // Yield once to be truly async
        await Task.Yield();

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // yield files first; collect subdirs for traversal
            var subdirs = new List<string>();

            await foreach (var e in _entries.EnumerateChildrenAsync(dir, ct))
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.Combine(e.DirPath, e.Name);
                var isDir = Directory.Exists(full); // keep behavior consistent with current enumerator

                if (isDir) subdirs.Add(full);
                else yield return e;
            }

            // DFS using your existing traversal behavior
            for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
    }
}


// // DuplicateFileFinderLib/Scan/DirectScanStrategy.cs
// using System.Collections.Generic;
// using System.IO.Enumeration;
// using System.Runtime.CompilerServices;
// using DuplicateFileFinderLib.Indexing;
//
// namespace DuplicateFileFinderLib.Scan;
//
// public sealed class DirectScanStrategy(EnumerationOptions? options = null) : IScanStrategy
// {
//     private readonly EnumerationOptions _opts = options ?? new EnumerationOptions
//     {
//         RecurseSubdirectories = true,
//         IgnoreInaccessible = true,
//         ReturnSpecialDirectories = false,
//         AttributesToSkip = FileAttributes.ReparsePoint, // skip symlinks/junctions
//         MatchType = MatchType.Simple
//     };
//
//     // skip symlinks/junctions
//
//     public async IAsyncEnumerable<FileEntryMeta> EnumerateAsync(
//         string root,
//         [EnumeratorCancellation] System.Threading.CancellationToken ct = default)
//     {
//         // Yield control once to keep the method truly async
//         await System.Threading.Tasks.Task.Yield();
//
//         var enumerable = new FileSystemEnumerable<FileEntryMeta>(
//             root,
//             static (ref FileSystemEntry e) =>
//             {
//                 // Project volume-relative directory path and file name without extra allocations
//                 ReadOnlySpan<char> full = e.ToFullPath();
//                 var name = GetFileName(full);
//                 var dir = full[..^name.Length];
//
//                 return new FileEntryMeta(
//                     DirPath: dir.ToString().TrimEnd(Path.DirectorySeparatorChar),
//                     Name: name.ToString(),
//                     SizeBytes: e.Length,
//                     MTimeUtc: e.LastWriteTimeUtc,
//                     CTimeUtc: e.CreationTimeUtc,
//                     Inode: TryGetInode(ref e),
//                     Mode: (int)(e.Attributes & (FileAttributes)0xFFFF)); // coarse mode from attributes
//             },
//             _opts)
//         {
//             // Only files; directories are traversed but not yielded
//             ShouldIncludePredicate = static (ref FileSystemEntry e) => !e.IsDirectory
//         };
//
//         foreach (var meta in enumerable)
//         {
//             if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
//             yield return meta;
//         }
//     }
//
//     // ---- helpers ----
//
//     private static ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path)
//     {
//         var idx = path.LastIndexOf(Path.DirectorySeparatorChar);
//         return idx >= 0 ? path[(idx + 1)..] : path;
//     }
//
//     private static ulong? TryGetInode(ref FileSystemEntry e)
//     {
//         // FileSystemEntry doesn't expose inode portably.
//         // TODO: implement P/Invoke to get inode
//         // Keep null; 
//         return null;
//     }
// }
