// DuplicateFileFinderLib/Repository/ScanSession.cs

using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public sealed class ScanSession : IAsyncDisposable, IScanSession
{
    private readonly Repo _repo;
    private readonly int _maxFilesBeforeFlush;
    private readonly int _maxDirsBeforeFlush;

    private readonly Lock _bufferLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    // Session-local view of all directories (both from repo snapshot and created during this scan)
    private readonly Dictionary<Guid, DirRecord> _dirsById;
    
    // Normalized full dir path -> DirId
    private readonly Dictionary<string, Guid> _dirPathIndex;

    // Normalized full file path -> FileRecord (latest state within this session)
    private readonly Dictionary<string, FileRecord> _filesByPath;

    private readonly StringComparer _pathComparer;
    
    // Buffered records for the next RepoDelta
    private readonly List<DirRecord> _pendingDirs = new();
    private readonly List<FileRecord> _pendingFiles = new();
    
    private bool _finished;
    
    public ScanSession(
        Repo repo,
        ScanRun run,
        IReadOnlyDictionary<Guid, DirRecord> existingDirsInRepo,
        int maxFilesBeforeFlush = 50_000,
        int maxDirsBeforeFlush  = 1_000)
    {
        _repo = repo;
        Run = run;
        _maxFilesBeforeFlush = maxFilesBeforeFlush;
        _maxDirsBeforeFlush = maxDirsBeforeFlush;

        _pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        _dirsById      = new Dictionary<Guid, DirRecord>(existingDirsInRepo.Count);
        _dirPathIndex = new Dictionary<string, Guid>(_pathComparer);
        _filesByPath   = new Dictionary<string, FileRecord>(_pathComparer);

        // Seed session dirs and path index from repo snapshot
        foreach (var (id, dir) in existingDirsInRepo)
        {
            _dirsById[id] = dir;

            var fullPath   = _repo.GetFullDirPath(id);
            var normalized = NormalizePath(fullPath);
            _dirPathIndex[normalized] = id;
        }
    }
    
    public ScanRun Run { get; }

    public long ScanSequence => Run.ScanSequence;
    public string RootPath => Run.RootPath;

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Best-effort drain; explicit Complete/Fail are the durable paths
            await FlushProgressAsync().ConfigureAwait(false);
        }
        catch
        {
            // swallow in Dispose
        }

        if (!_finished)
            _repo.MarkScanFailed(ScanSequence, "ScanSession disposed before completion.", cancelled: true);
    }

    // ---------------------------------------------------------------------
    // Path helpers
    // ---------------------------------------------------------------------

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is null or empty", nameof(path));

        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // ---------------------------------------------------------------------
    // Directory upsert (path-based)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Ensure that the directory at <paramref name="fullPath"/> has a stable DirRecord.Id.
    /// Creates any missing parents as dummy dirs (Status=None), and the leaf with the
    /// requested <paramref name="status"/> (or a default if null). Returns the DirId.
    /// </summary>
    public Guid AddOrUpdateDirectory(
        string fullPath,
        ScanEntryStatus? status       = null,
        string? errorMessage = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new ArgumentException("Path is null or empty", nameof(fullPath));

        fullPath = NormalizePath(fullPath);

        var shouldFlush = false;

        // Fast path: we already know this path (from repo or this session)
        if (_dirPathIndex.TryGetValue(fullPath, out var existingId))
        {
            UpdateExistingDir(existingId, status, errorMessage, ref shouldFlush);

            if (shouldFlush)
                _ = FlushProgressAsync();

            return existingId;
        }

        // Build the chain of unknown paths from leaf up to the first known ancestor
        var toCreate = new Stack<string>();
        var current  = fullPath;

        while (true)
        {
            if (_dirPathIndex.ContainsKey(current))
                break;

            toCreate.Push(current);

            var parentPath = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parentPath) || _pathComparer.Equals(parentPath, current))
                break;

            current = NormalizePath(parentPath);
        }

        Guid? parentId = null;

        // If we stopped because we hit a known parent, remember its Id
        if (_dirPathIndex.TryGetValue(current, out var knownParentId))
            parentId = knownParentId;

        // Create missing parents and the leaf
        while (toCreate.Count > 0)
        {
            var path = toCreate.Pop();
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
            {
                // Root cases like "C:\" or "/"
                name = path;
            }

            var isLeaf = _pathComparer.Equals(path, fullPath);

            // For new leaf: default status is Enumerated if not provided.
            // For parents: Status.None and no error message.
            var effectiveStatus = isLeaf
                ? (status ?? ScanEntryStatus.Enumerated)
                : ScanEntryStatus.None;

            var effectiveError = isLeaf ? errorMessage : null;

            var id = Guid.NewGuid();

            var dir = new DirRecord
            {
                Id               = id,
                ParentId         = parentId,
                Name             = name,
                LastSeenSequence = ScanSequence,
                Status           = effectiveStatus,
                ErrorMessage     = effectiveError
            };

            parentId = id;

            _dirsById[id] = dir;

            lock (_bufferLock)
            {
                _pendingDirs.Add(dir);
                _dirPathIndex[path] = id;

                if (_pendingDirs.Count >= _maxDirsBeforeFlush)
                    shouldFlush = true;
            }
        }

        var leafId = parentId!.Value;

        if (shouldFlush)
            _ = FlushProgressAsync();

        return leafId;
    }

    private void UpdateExistingDir(
        Guid             id,
        ScanEntryStatus? status,
        string?          errorMessage,
        ref bool         shouldFlush)
    {
        if (!_dirsById.TryGetValue(id, out var existing))
            return;
        
        var newStatus = status ?? existing.Status;
        var newError  = errorMessage ?? existing.ErrorMessage;

        var updated = existing with
        {
            LastSeenSequence = ScanSequence,
            Status           = newStatus,
            ErrorMessage     = newError
        };

        _dirsById[id] = updated;

        lock (_bufferLock)
        {
            _pendingDirs.Add(updated);

            if (_pendingDirs.Count >= _maxDirsBeforeFlush)
                shouldFlush = true;
        }
    }

    // ---------------------------------------------------------------------
    // File upsert (path-based)
    // ---------------------------------------------------------------------

    public void AddOrUpdateFile(
        string fullFilePath,
        long?            size         = null,
        HashKey?         hash         = null,
        DateTimeOffset?  modified     = null,
        DateTimeOffset?  created      = null,
        ScanEntryStatus? status       = null,
        string? errorMessage = null)
    {
        if (string.IsNullOrWhiteSpace(fullFilePath))
            throw new ArgumentException("File path is null or empty", nameof(fullFilePath));

        fullFilePath = NormalizePath(fullFilePath);

        var dirPath = Path.GetDirectoryName(fullFilePath);
        if (string.IsNullOrEmpty(dirPath))
            dirPath = RootPath;

        // Ensure directory chain exists and leaf dir is at least Enumerated
        var dirId = AddOrUpdateDirectory(dirPath, ScanEntryStatus.Enumerated);
        var name  = Path.GetFileName(fullFilePath);
        if (string.IsNullOrEmpty(name))
            name = fullFilePath;

        var shouldFlush = false;

        lock (_bufferLock)
        {
            FileRecord file;

            if (_filesByPath.TryGetValue(fullFilePath, out var existing))
            {
                var newSize    = size    ?? existing.Size;
                var newHash    = hash    ?? existing.Hash;
                var newMod     = modified ?? existing.Modified;
                var newCreated = created  ?? existing.Created;
                var newStatus  = status   ?? existing.Status;
                var newError   = errorMessage ?? existing.ErrorMessage;

                file = existing with
                {
                    DirId               = dirId,
                    Name                = name,
                    Size                 = newSize,
                    Hash                 = newHash,
                    Modified             = newMod,
                    Created              = newCreated,
                    LastSeenScanSequence = ScanSequence,
                    Status               = newStatus,
                    ErrorMessage         = newError
                };
            }
            else
            {
                var effectiveSize    = size    ?? 0L;
                var effectiveHash    = hash    ?? default(HashKey);
                var effectiveMod     = modified ?? default(DateTimeOffset);
                var effectiveCreated = created  ?? default(DateTimeOffset);
                var effectiveStatus  = status   ?? ScanEntryStatus.Enumerated;

                file = new FileRecord
        {
            Id                  = Guid.NewGuid(),
            DirId               = dirId,
            Name                = name,
                    Size                 = effectiveSize,
                    Hash                 = effectiveHash,
                    Modified             = effectiveMod,
                    Created              = effectiveCreated,
            LastSeenScanSequence = ScanSequence,
                    Status               = effectiveStatus,
            ErrorMessage        = errorMessage
        };
            }

            _filesByPath[fullFilePath] = file;
            _pendingFiles.Add(file);

            if (_pendingFiles.Count >= _maxFilesBeforeFlush)
                shouldFlush = true;
        }

        if (shouldFlush)
            _ = FlushProgressAsync();
    }

    // ---------------------------------------------------------------------
    // Legacy APIs (wrappers)
    // ---------------------------------------------------------------------

    [Obsolete("Use AddOrUpdateDirectory instead.")]
    public Guid ObserveDirectory(
        string          fullPath,
        ScanEntryStatus status,
        string?         errorMessage = null)
    {
        return AddOrUpdateDirectory(fullPath, status, errorMessage);
    }

    [Obsolete("Use AddOrUpdateFile instead.")]
    public void ObserveFile(
        string          fullFilePath,
        long            size,
        HashKey         hash,
        DateTimeOffset  modified,
        DateTimeOffset  created,
        ScanEntryStatus status,
        string?         errorMessage = null)
    {
        AddOrUpdateFile(fullFilePath, size, hash, modified, created, status, errorMessage);
    }

    // ---------------------------------------------------------------------
    // Flush
    // ---------------------------------------------------------------------

    public Task FlushProgressAsync(CancellationToken cancellationToken = default) =>
        FlushProgressInternalAsync(cancellationToken);

    private async Task FlushProgressInternalAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DirRecord>  dirsToFlush;
            List<FileRecord> filesToFlush;

            lock (_bufferLock)
            {
                if (_pendingDirs.Count == 0 && _pendingFiles.Count == 0)
                    return;

                dirsToFlush  = new List<DirRecord>(_pendingDirs);
                filesToFlush = new List<FileRecord>(_pendingFiles);

                _pendingDirs.Clear();
                _pendingFiles.Clear();
            }

            var delta = new RepoDelta
            {
                ScanSequence = ScanSequence,
                Dirs         = dirsToFlush,
                Files        = filesToFlush,
                DeletedDirs  = new List<DirTombstone>(),
                DeletedFiles = new List<FileTombstone>()
            };

            await _repo.CommitDeltaAsync(delta, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // ---------------------------------------------------------------------
    // Completion / failure
    // ---------------------------------------------------------------------

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        _repo.CompleteScanForRoot(ScanSequence, Run.RootPath);
        _finished = true;
    }

    public async Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
    {
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        _repo.MarkScanFailed(ScanSequence, errorMessage, cancelled);
        _finished = true;
    }
}
