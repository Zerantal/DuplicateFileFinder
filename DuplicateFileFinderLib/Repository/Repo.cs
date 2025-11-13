// DuplicateFileFinderLib/Repo/Repo.cs

using System.Text.Json;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using NLog;

namespace DuplicateFileFinderLib.Repository;

/// <summary>
/// The persistent database of all scanned files across all scan locations.
/// Uses a snapshot + append-only delta log for durability.
/// </summary>
public sealed class Repo
{
    private const int RepoSchemaVersion = 2;
    
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _rootPath;
    private readonly string _metaFile;
    private readonly string _snapshotFile;
    private readonly string _logDir;
    
    public RepoMeta Meta { get; private set; } = new();
    public Dictionary<Guid, FileRecord> Files { get; private set; } = new();
    public Dictionary<Guid, DirRecord> Dirs { get; private set; } = new();
    public Dictionary<string, Guid> InternedStrings { get; private set; } = new();

    // HashBytes -> FileIds
    public Dictionary<HashKey, List<Guid>> HashIndex { get; private set; } = new();

    // Use a field for atomic increments. Mirror to Meta.NextSequence on SaveMeta.
    private long _nextSeq;

    // to sync file+meta mutations.
    private readonly object _sync = new();
    
    public event EventHandler<RepoDelta>? DeltaCommitted;
    
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
        using (TimingLog.StartPhase("Opening Repo"))
        {
            repo.LoadMeta();
            repo.LoadSnapshot(); // 1) data image
            repo.ReplayDeltas(); // 3) bring forward by deltas; ApplyDelta updates HashIndex
            repo._nextSeq = repo.Meta.NextSequence;
            TimingLog.Counter("files", repo.Files.Count);
            TimingLog.Counter("dirs", repo.Dirs.Count);
            TimingLog.Counter("HashIndex", repo.HashIndex.Count);
        }
        
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
        
        DeltaCommitted?.Invoke(this, delta);

    }

    public void SaveSnapshot()
    {
        lock (_sync)
        {
            SaveSnapshot_NoLock();
        }
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
        Meta.SchemaVersion = RepoSchemaVersion; // update if schema changes
        var snapshot = new RepoSnapshotV2(Meta, Files, Dirs, InternedStrings, HashIndex);

        var tmp = _snapshotFile + ".tmp";
        var bytes = MemoryPackSerializer.Serialize(snapshot);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, _snapshotFile, overwrite: true);

        // Mark that this snapshot includes all deltas up to current NextSequence
        Meta.LastSnapshottedSequence = Meta.NextSequence;
        SaveMeta_NoLock();
        
    }

    // ---------- loader pieces ----------

    private void LoadSnapshot()
    {
        Files.Clear(); Dirs.Clear(); InternedStrings.Clear(); HashIndex.Clear();
        if (!File.Exists(_snapshotFile)) return;
        
        var bytes = File.ReadAllBytes(_snapshotFile);
        
        // Try V2
        try
        {
            var v2 = MemoryPackSerializer.Deserialize<RepoSnapshotV2>(bytes);
            if (v2 is not null && (v2.Meta.SchemaVersion >= 2))
            {
                Meta = v2.Meta;
                Files = v2.Files;
                Dirs = v2.Dirs;
                InternedStrings = v2.Strings;
                HashIndex = v2.HashIndex;
                return;
            }
        }
        catch (MemoryPackSerializationException)
        {
            Log.Error("Failed to load repo (V2 format).");
        }

        Log.Info("Attempting to load repo (V1 format).");
        // Fallback: load your previous V1, then rebuild index once.
        try
        {
            var v1 = MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes);
            if (v1 == null) throw new InvalidDataException("Snapshot corrupted");

            Meta = v1.Meta;
            foreach (var f in v1.Files) Files[f.Id] = f;
            foreach (var d in v1.Dirs) Dirs[d.Id] = d;
            foreach (var kv in v1.Strings) InternedStrings[kv.Key] = kv.Value;
            foreach (var f in v1.Files)
            {
                var key = HashKey.From(f.Hash);
                if (!HashIndex.TryGetValue(key, out var list)) HashIndex[key] = list = new List<Guid>(4);
                list.Add(f.Id);
            }
        }
        catch (MemoryPackSerializationException e)
        {
            Log.Error(e, "Failed to load repo (V1 format).");
            throw;
        }
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
            var key = HashKey.From(f.Hash);
            if (!HashIndex.TryGetValue(key, out var list))
                HashIndex[key] = list = new List<Guid>(4);
            // guard against dup if same delta is re-applied
            if (list.Count == 0 || list[^1] != f.Id) // cheap common case
                if (!list.Contains(f.Id)) list.Add(f.Id);
        }
        foreach (var d in delta.Dirs) Dirs[d.Id] = d;
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

        // 1) Write a fresh snapshot
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