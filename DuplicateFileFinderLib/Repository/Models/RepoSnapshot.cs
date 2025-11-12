using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record RepoSnapshot(
    RepoMeta Meta,
    List<FileRecord> Files,
    List<DirRecord> Dirs,
    IDictionary<string, Guid> Strings);
    
[MemoryPackable]
public partial record RepoSnapshotV2(
    RepoMeta Meta,
    Dictionary<Guid, FileRecord> Files,
    Dictionary<Guid, DirRecord> Dirs,
    Dictionary<string, Guid> Strings,
    Dictionary<HashKey, List<Guid>> HashIndex
);