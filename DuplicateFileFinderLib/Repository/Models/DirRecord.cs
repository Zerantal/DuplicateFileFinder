using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record DirRecord(
    Guid Id, 
    Guid? ParentId, 
    string Name);