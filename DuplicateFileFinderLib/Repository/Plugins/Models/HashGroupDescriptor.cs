// DuplicateFileFinderLib/Repository/Plugins/Models/HashGroupDescriptor.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

public readonly record struct HashGroupDescriptor(
    HashKey Hash,
    long SizeBytes,   // Size in bytes of each file in the group
    int Offset,
    int Count,
    FileHandle FirstFile)
{
    internal int Offset { get; init; } = Offset;
}

