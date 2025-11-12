using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record RepoDelta(
    List<FileRecord> Files,
    List<DirRecord> Dirs);
