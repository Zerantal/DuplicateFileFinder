using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record RepoIndexes(
    long Generation,
    List<HashBucket> Buckets
);