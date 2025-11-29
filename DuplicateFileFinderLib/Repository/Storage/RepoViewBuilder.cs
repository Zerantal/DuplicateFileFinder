using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Storage;

public static class RepoViewBuilder
{
    public static RepoViewSnapshot BuildForSingleRoot(ScanRootSnapshotOnDisk rootSnap)
    {
        var fileDict = new Dictionary<Guid, FileRecord>(rootSnap.Files.Length);
        foreach (var f in rootSnap.Files)
            fileDict[f.Id] = f;

        var dirDict = new Dictionary<Guid, DirRecord>(rootSnap.Dirs.Length);
        foreach (var d in rootSnap.Dirs)
            dirDict[d.Id] = d;

        var hashIndex = BuildHashIndex(fileDict);

        return new RepoViewSnapshot
        {
            Files = fileDict,
            Dirs  = dirDict,
            HashIndex = hashIndex
        };
    }

    private static IReadOnlyDictionary<HashKey, IReadOnlyList<Guid>> BuildHashIndex(
        IReadOnlyDictionary<Guid, FileRecord> files)
    {
        var tmp = new Dictionary<HashKey, List<Guid>>();

        foreach (var kvp in files)
        {
            var f = kvp.Value;
            if (!f.Hash.IsComputed) // skip NotComputed / CannotCompute
                continue;

            if (!tmp.TryGetValue(f.Hash, out var list))
            {
                list = new List<Guid>();
                tmp[f.Hash] = list;
            }

            list.Add(f.Id);
        }

        var result = new Dictionary<HashKey, IReadOnlyList<Guid>>(tmp.Count);
        foreach (var kvp in tmp)
            result[kvp.Key] = kvp.Value;

        return result;
    }
}