using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record RepoSnapshot(
    RepoMeta Meta,
    List<FileRecord> Files,
    List<DirRecord> Dirs,
    IDictionary<string, Guid> Strings);