namespace MemPackBench.FileDirIndexStateBench;

public static class Conversions
{
    public static FileDirIndexStateV2 ToV2(FileDirIndexStateV1 v1)
        => new()
        {
            LastIndexedGeneration = v1.LastIndexedGeneration,
            LastIndexedLogSequence = v1.LastIndexedLogSequence,
            DirsById = v1.DirsById.ToArray(),
            FilesById = v1.FilesById.ToArray(),
        };

    public static FileDirIndexStateV1 ToV1(FileDirIndexStateV2 v2)
    {
        // Pre-size to avoid rehash churn.
        var dirs = new Dictionary<long, DirHandle>(capacity: v2.DirsById.Length);
        var files = new Dictionary<long, FileHandle>(capacity: v2.FilesById.Length);
        
        foreach (var kv in v2.DirsById)
            dirs[kv.Key] = kv.Value;
        
        foreach (var kv in v2.FilesById)
            files[kv.Key] = kv.Value;

        return new FileDirIndexStateV1
        {
            LastIndexedGeneration = v2.LastIndexedGeneration,
            LastIndexedLogSequence = v2.LastIndexedLogSequence,
            DirsById = dirs,
            FilesById = files,
        };
    }
}