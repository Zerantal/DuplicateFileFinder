// DuplicateFileFinderLib/Repo/Repo.cs

using System.Collections.Concurrent;
using System.Text.Json;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository;

/// <summary>
/// The persistent database of all scanned files across all scan locations.
/// Uses a snapshot + append-only delta log for durability.
/// </summary>
public sealed class Repo
{
    private readonly string _rootPath;
    private readonly string _metaFile;
    private readonly string _snapshotFile;
    private readonly string _logDir;

    public RepoMeta Meta { get; private set; } = new();
    public ConcurrentDictionary<Guid, FileRecord> Files { get; } = new();
    public ConcurrentDictionary<Guid, DirRecord> Dirs { get; } = new();
    public ConcurrentDictionary<string, Guid> InternedStrings { get; } = new();

    // HashBytes -> FileIds
    public ConcurrentDictionary<ReadOnlyMemory<byte>, List<Guid>> HashIndex { get; } = new(new MemoryComparer());

    // Use a field for atomic increments. Mirror to Meta.NextSequence on SaveMeta.
    private long _nextSeq;
    private string IndexesPath => Path.Combine(_rootPath, $"indexes-{Meta.Generation}.bin");

    // to sync file+meta mutations.
    private readonly object _sync = new();
    
    private Repo(string rootPath)
    {
        _rootPath = rootPath;
        _metaFile = Path.Combine(rootPath, "meta.json");
        _snapshotFile = Path.Combine(rootPath, "snapshot.bin");
        _logDir = Path.Combine(rootPath, "log");
        Directory.CreateDirectory(_logDir);
    }

    public static Repo Open(string rootPath)
    {
        var repo = new Repo(rootPath);
        repo.LoadMeta();
        repo.LoadSnapshot();                 // 1) data image
        if (!repo.LoadIndexes())             // 2) prebuilt hash index for this generation
            repo.RebuildIndexesAndPersist(); //    or rebuild and persist
        repo.ReplayDeltas();                 // 3) bring forward by deltas; ApplyDelta updates HashIndex
        repo._nextSeq = repo.Meta.NextSequence;
        return repo;
    }

    // ---------- public ops ----------

    public ScanSession BeginScan(int scanId, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException(nameof(rootPath));
        return new ScanSession(this, scanId, rootPath);
    }

    public void CommitDelta(RepoDelta delta)
    {
        var seq = Interlocked.Increment(ref _nextSeq);
        var tmp = Path.Combine(_logDir, $"{Meta.Generation}-{seq}.tmp");
        var final = Path.Combine(_logDir, $"{Meta.Generation}-{seq}.delta");

        var bytes = MemoryPackSerializer.Serialize(delta);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, final, overwrite: true);

        ApplyDelta(delta);

        Meta.NextSequence = _nextSeq;
        SaveMeta_NoLock();
    }

    public void SaveSnapshot()
    {
        lock (_sync)
        {
            SaveSnapshot_NoLock();
        }
    }
    
    // Rebuild indexes helpers
    private void RebuildIndexesAndPersist_NoLock()
    {
        HashIndex.Clear();
        foreach (var f in Files.Values)
            HashIndex.GetOrAdd(f.Hash, _ => new()).Add(f.Id);

        var buckets = new List<HashBucket>(HashIndex.Count);
        foreach (var kv in HashIndex)
            buckets.Add(new HashBucket(kv.Key.ToArray(), kv.Value.ToArray()));

        var idx = new RepoIndexes(Generation: Meta.Generation, Buckets: buckets);
        var tmp = IndexesPath + ".tmp";
        var bytes = MemoryPackSerializer.Serialize(idx);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, IndexesPath, overwrite: true);
    }
    
    private void SaveMeta_NoLock()
    {
        var json = JsonSerializer.Serialize(Meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_metaFile, json);
        Fsync(_metaFile);
    }
    
// Writes snapshot + indexes and updates meta. Caller must hold _sync.
    private void SaveSnapshot_NoLock()
    {
        var snapshot = new RepoSnapshot(
            Meta,
            Files.Values.ToList(),
            Dirs.Values.ToList(),
            new Dictionary<string, Guid>(InternedStrings));

        var tmp = _snapshotFile + ".tmp";
        var bytes = MemoryPackSerializer.Serialize(snapshot);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, _snapshotFile, overwrite: true);

        // Mark that this snapshot includes all deltas up to current NextSequence
        Meta.LastSnapshottedSequence = Meta.NextSequence;
        SaveMeta_NoLock();

        // Rebuild and persist indexes for this image
        RebuildIndexesAndPersist_NoLock();
    }

    // ---------- loader pieces ----------

    private void LoadSnapshot()
    {
        if (!File.Exists(_snapshotFile))
        {
            // empty repo
            Files.Clear(); Dirs.Clear(); InternedStrings.Clear(); HashIndex.Clear();
            return;
        }

        var bytes = File.ReadAllBytes(_snapshotFile);
        var snapshot = MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes)
                       ?? throw new InvalidDataException("Snapshot corrupted");

        Meta = snapshot.Meta;

        Files.Clear(); Dirs.Clear(); InternedStrings.Clear(); HashIndex.Clear();

        foreach (var f in snapshot.Files)
            Files[f.Id] = f;

        foreach (var d in snapshot.Dirs)
            Dirs[d.Id] = d;

        foreach (var kv in snapshot.Strings)
            InternedStrings[kv.Key] = kv.Value;
    }

    private bool LoadIndexes()
    {
        var path = IndexesPath;
        if (!File.Exists(path)) return false;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var idx = MemoryPackSerializer.Deserialize<RepoIndexes>(bytes);
            if (idx == null || idx.Generation != Meta.Generation) return false;

            HashIndex.Clear();
            foreach (var b in idx.Buckets)
            {
                // Copy to ReadOnlyMemory keys backed by byte[]
                var key = (ReadOnlyMemory<byte>)b.Hash;
                HashIndex[key] = new List<Guid>(b.FileIds);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RebuildIndexesAndPersist()
    {
        // Rebuild in-memory
        HashIndex.Clear();
        foreach (var f in Files.Values)
            HashIndex.GetOrAdd(f.Hash, _ => new()).Add(f.Id);

        // Persist compact index file for this generation
        var buckets = new List<HashBucket>(HashIndex.Count);
        foreach (var kv in HashIndex)
        {
            // Make sure hash bytes are materialized as byte[] for serialization
            var hashBytes = kv.Key.ToArray();
            var ids = kv.Value.ToArray();
            buckets.Add(new HashBucket(hashBytes, ids));
        }
        var idx = new RepoIndexes(Generation: Meta.Generation, Buckets: buckets);

        var tmp = IndexesPath + ".tmp";
        var bytes = MemoryPackSerializer.Serialize(idx);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, IndexesPath, overwrite: true);
    }

    private void ReplayDeltas()
    {
        if (!Directory.Exists(_logDir)) return;

        var files = Directory.GetFiles(_logDir, $"{Meta.Generation}-*.delta")
            .OrderBy(f => f, StringComparer.Ordinal);
        
        foreach (var path in files)
        {
            // filename pattern: "<gen>-<seq>.delta"
            var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<seq>"
            var dash = name.IndexOf('-');
            if (dash <= 0) continue;

            var seqPart = name[(dash + 1)..];
            if (long.TryParse(seqPart, out var seq))
            {
                // Only skip deltas already captured in the snapshot
                if (seq <= Meta.LastSnapshottedSequence)
                    continue;
            }
            
            var bytes = File.ReadAllBytes(path);
            var delta = MemoryPackSerializer.Deserialize<RepoDelta>(bytes);
            if (delta != null) ApplyDelta(delta);
        }
    }

    private void ApplyDelta(RepoDelta delta)
    {
        foreach (var f in delta.Files)
        {
            Files[f.Id] = f;
            
            var list = HashIndex.GetOrAdd(f.Hash, _ => new());
            // avoid duplicates if the same delta is applied again
            if (!list.Contains(f.Id))
                list.Add(f.Id);
        }
        foreach (var d in delta.Dirs)
            Dirs[d.Id] = d;
    }

    // ---------- Compaction ---------

public void CompactIfNeeded(RepoCompactionPolicy? policy = null)
{
    policy ??= new RepoCompactionPolicy();

    // Fast path: compute sizes without locking
    var (logBytes, deltaCount) = GetLogSizeAndCount();
    var snapBytes = File.Exists(_snapshotFile) ? new FileInfo(_snapshotFile).Length : 0L;

    if (!ShouldCompact(policy, logBytes, deltaCount, snapBytes))
        return;

    // Serialize with other writers
    lock (_sync)
    {
        // Recompute under lock to avoid TOCTOU
        (logBytes, deltaCount) = GetLogSizeAndCount();
        snapBytes = File.Exists(_snapshotFile) ? new FileInfo(_snapshotFile).Length : 0L;
        if (!ShouldCompact(policy, logBytes, deltaCount, snapBytes))
            return;

        // 1) Write a fresh snapshot + indexes
        SaveSnapshot_NoLock(); // sets Meta.LastSnapshottedSequence = Meta.NextSequence and persists meta

        // 2) Delete deltas already captured in snapshot
        DeleteObsoleteDeltas_NoLock();
    }
}

public void CompactNow()
{
    lock (_sync)
    {
        SaveSnapshot_NoLock();
        DeleteObsoleteDeltas_NoLock();
    }
}



private static bool ShouldCompact(RepoCompactionPolicy policy, long logBytes, int deltaCount, long snapBytes)
{
    if (deltaCount < policy.MinDeltaCount) return false;
    if (logBytes < policy.MinLogBytes) return false;

    if (snapBytes <= 0) return true; // no snapshot yet → compact

    var ratio = (double)logBytes / Math.Max(1L, snapBytes);
    return ratio >= policy.RatioThreshold;
}

// Deletes all deltas with seq ≤ LastSnapshottedSequence. Caller must hold _sync.
private void DeleteObsoleteDeltas_NoLock()
{
    if (!Directory.Exists(_logDir)) return;

    foreach (var path in Directory.GetFiles(_logDir, $"{Meta.Generation}-*.delta"))
    {
        var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<seq>"
        var dash = name.IndexOf('-');
        if (dash <= 0) continue;
        var seqPart = name[(dash + 1)..];
        if (long.TryParse(seqPart, out var seq) && seq <= Meta.LastSnapshottedSequence)
        {
            try { File.Delete(path); } catch { /* tolerate */ }
        }
    }
}

    
    
    // ---------- meta I/O ----------

    private void LoadMeta()
    {
        if (File.Exists(_metaFile))
        {
            Meta = JsonSerializer.Deserialize<RepoMeta>(File.ReadAllText(_metaFile)) ?? new RepoMeta();
        }
        else
        {
            SaveMeta();
        }
    }

    private void SaveMeta()
    {
        var json = JsonSerializer.Serialize(Meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_metaFile, json);
        Fsync(_metaFile);
    }

    // ---------- util ----------

    private static void Fsync(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        fs.Flush(flushToDisk: true);
    }
    
    private (long logBytes, int count) GetLogSizeAndCount()
    {
        if (!Directory.Exists(_logDir)) return (0L, 0);
        long bytes = 0;
        int count = 0;
        foreach (var p in Directory.GetFiles(_logDir, $"{Meta.Generation}-*.delta"))
        {
            var fi = new FileInfo(p);
            if (fi.Exists) { bytes += fi.Length; count++; }
        }
        return (bytes, count);
    }
}

file sealed class MemoryComparer : IEqualityComparer<ReadOnlyMemory<byte>>
{
    public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) => x.Span.SequenceEqual(y.Span);

    public int GetHashCode(ReadOnlyMemory<byte> obj)
    {
        var span = obj.Span;
        unchecked
        {
            int h = 17;
            for (int i = 0; i < span.Length; i++) h = h * 31 + span[i];
            return h;
        }
    }
}
