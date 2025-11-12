using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record HashBucket(
    byte[] Hash,
    Guid[] FileIds
);