// DuplicateFileFinderLib/Repository/Plugins/Models/FolderGroupDescriptor.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

public readonly record struct FolderGroupDescriptor(
    HashKey FolderHash,
    int Offset,
    int Count,
    DirHandle FirstDir,
    int ChildFileCount,
    int ChildDirCount);

