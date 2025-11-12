using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record RepoMeta
{
    public int SchemaVersion { get; set; } = 1;
    public long Generation { get; set; } = 1;
    public long NextSequence { get; set; } = 0;
    
    public long LastSnapshottedSequence { get; set; } = 0;
    
    public DateTimeOffset LastCompaction { get; set; } = DateTimeOffset.UtcNow;
}