// DuplicateFileFinderLib/Repo/ScanSession.cs

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository;

/// <summary>
/// Collects new/updated nodes for a single scan and emits a RepoDelta in one commit.
/// Deterministic IDs: DirId = GUID(md5(normalizedDirPath)); FileId = GUID(md5(normalizedFullPath)).
/// This makes upserts idempotent: later scans overwrite by key.
/// </summary>
public sealed class ScanSession : IDisposable
{
    private readonly Repo _repo;
    private readonly int _scanId;
    private readonly string _rootPathNorm;

    // Buffers for this session
    private readonly Dictionary<Guid, DirRecord> _dirBuffer = new();
    private readonly Dictionary<Guid, FileRecord> _fileBuffer = new();

    // Cache to avoid rebuilding parent chains repeatedly
    private readonly ConcurrentDictionary<string, Guid> _dirIdCache = new(StringComparer.Ordinal);

    // Thresholds for opportunistic flush into a delta file
    private readonly int _fileFlushThreshold;
    private readonly int _dirFlushThreshold;

    private bool _disposed;

    public ScanSession(Repo repo, int scanId, string rootPath, int fileFlushThreshold = 50_000, int dirFlushThreshold = 10_000)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _scanId = scanId;
        _rootPathNorm = PathUtils.NormalizePath(rootPath);

        _fileFlushThreshold = fileFlushThreshold;
        _dirFlushThreshold = dirFlushThreshold;

        // Ensure the root directory node exists in this session
        var rootDirId = EnsureDir(_rootPathNorm);
        _ = rootDirId;
    }

    /// <summary>
    /// Add or update a directory by absolute path. Creates ancestors on demand.
    /// Returns the deterministic DirId.
    /// </summary>
    public Guid EnsureDir(string dirPath)
    {
        dirPath = PathUtils.NormalizePath(dirPath);

        if (_dirIdCache.TryGetValue(dirPath, out var cached))
            return cached;

        // Build parents from root to leaf
        var parts = PathUtils.SplitPath(dirPath);
        var acc = new List<string>(parts.Count);
        Guid? parent = null;
        for (int i = 0; i < parts.Count; i++)
        {
            acc.Add(parts[i]);
            var curPath = "/" + string.Join(Path.DirectorySeparatorChar, acc);
            if (!_dirIdCache.TryGetValue(curPath, out var id))
            {
                id = StableGuid(curPath);
                var name = parts[i];
                var parentId = parent;
                var dir = new DirRecord(id, parentId, name);
                _dirBuffer[id] = dir;               // upsert
                _dirIdCache[curPath] = id;
            }
            parent = _dirIdCache[curPath];
        }
        return _dirIdCache[dirPath];
    }

    /// <summary>
    /// Upsert a file by absolute path.
    /// </summary>
    public void UpsertFile(string fullPath,
                           long size,
                           ReadOnlySpan<byte> hash,
                           DateTimeOffset modified,
                           DateTimeOffset created)
    {
        var normPath = PathUtils.NormalizePath(fullPath);
        var dirPath  = Path.GetDirectoryName(normPath) ?? _rootPathNorm;
        var name     = Path.GetFileName(normPath);

        var dirId = EnsureDir(dirPath);
        var fileId = StableGuid(normPath);

        // Copy hash to small array
        var hashCopy = new byte[hash.Length];
        hash.CopyTo(hashCopy);

        var fr = new FileRecord(
            Id:        fileId,
            DirId:     dirId,
            Name:      name,
            Size:      size,
            Hash:      hashCopy,
            Modified:  modified,
            Created:   created,
            ScanId:    _scanId
        );

        _fileBuffer[fileId] = fr; // upsert

        // Opportunistic flush if buffers are large
        if (_fileBuffer.Count >= _fileFlushThreshold || _dirBuffer.Count >= _dirFlushThreshold)
            FlushDelta();
    }

    /// <summary>
    /// Writes remaining buffered changes as a single delta and clears buffers.
    /// Safe to call multiple times.
    /// </summary>
    public void FlushDelta()
    {
        if (_fileBuffer.Count == 0 && _dirBuffer.Count == 0) return;

        var delta = new RepoDelta(
            Files: _fileBuffer.Values.ToList(),
            Dirs:  _dirBuffer.Values.ToList()
        );

        _repo.CommitDelta(delta);

        _fileBuffer.Clear();
        _dirBuffer.Clear();
    }

    /// <summary>
    /// Flush and dispose.
    /// </summary>
    public void Commit()
    {
        FlushDelta();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Commit(); }
        finally { _disposed = true; }
    }

    // ---------- helpers ----------

    private static Guid StableGuid(string input)
    {
        // MD5 bytes → GUID. Stable across runs. Not for security.
        Span<byte> md5 = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(input), md5);
        return new Guid(md5);
    }
}
