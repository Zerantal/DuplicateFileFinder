using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record FileRecord(
    Guid Id,
    Guid DirId,
    string Name,
    long Size,
    byte[] Hash,
    DateTimeOffset Modified,
    DateTimeOffset Created,
    int ScanId);