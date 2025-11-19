using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public sealed class ScanSession : IAsyncDisposable, IScanSession
{
    private readonly Lock _bufferLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly int _maxDirsBeforeFlush;
    private readonly int _maxFilesBeforeFlush;

    private readonly List<DirRecord> _pendingDirs = new();

    // buffers
    private readonly List<FileRecord> _pendingFiles = new();
    private readonly Repo _repo;

    public ScanSession(
        Repo repo,
        ScanRun run,
        int maxFilesBeforeFlush = 10_000,
        int maxDirsBeforeFlush = 1000)
    {
        _repo = repo;
        Run = run;

        _maxFilesBeforeFlush = maxFilesBeforeFlush;
        _maxDirsBeforeFlush = maxDirsBeforeFlush;
    }

    public async ValueTask DisposeAsync()
    {
        // Best-effort drain; if caller already completed/failed, this is mostly a no-op
        try
        {
            await FlushProgressAsync().ConfigureAwait(false);
        }
        catch
        {
            // Swallow in Dispose; explicit CompleteAsync/FailAsync are the durable paths
        }

        if (Run.Status == ScanRunStatus.InProgress)
            _repo.MarkScanFailed(ScanSequence, "ScanSession disposed before completion.", true);
    }

    public ScanRun Run { get; }

    public long ScanSequence => Run.ScanSequence;

    public string RootPath => Run.RootPath;

    // -------- Observation APIs --------

    public void ObserveDir(
        Guid id,
        Guid? parentId,
        string name,
        ScanEntryStatus status,
        string? errorMessage = null)
    {
        var dir = new DirRecord
        {
            Id = id,
            ParentId = parentId,
            Name = name,
            LastSeenSequence = ScanSequence,
            Status = status,
            ErrorMessage = errorMessage
        };

        bool shouldFlush;
        lock (_bufferLock)
        {
            _pendingDirs.Add(dir);
            shouldFlush = _pendingDirs.Count >= _maxDirsBeforeFlush;
        }

        if (shouldFlush)
            _ = FlushProgressAsync(); // fire-and-forget; completion paths await explicitly
    }

    public void ObserveFile(
        Guid id,
        Guid dirId,
        string name,
        long size,
        HashKey hash,
        DateTimeOffset modified,
        DateTimeOffset created,
        ScanEntryStatus status,
        string? errorMessage = null)
    {
        var file = new FileRecord
        {
            Id = id,
            DirId = dirId,
            Name = name,
            Size = size,
            Hash = hash,
            Modified = modified,
            Created = created,
            LastSeenScanSequence = ScanSequence,
            Status = status,
            ErrorMessage = errorMessage
        };

        bool shouldFlush;
        lock (_bufferLock)
        {
            _pendingFiles.Add(file);
            shouldFlush = _pendingFiles.Count >= _maxFilesBeforeFlush;
        }

        if (shouldFlush)
            _ = FlushProgressAsync();
    }

    // -------- Async flush --------

    public Task FlushProgressAsync(CancellationToken cancellationToken = default)
    {
        return FlushProgressInternalAsync(cancellationToken);
    }

    // -------- Completion / failure / disposal --------

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        // Ensure all buffered + in-flight auto-flush work is drained
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        _repo.CompleteScanForRoot(ScanSequence, Run.RootPath);
    }

    public async Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
    {
        // Flush any positive progress, but do NOT tombstone on failure
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        _repo.MarkScanFailed(ScanSequence, errorMessage, cancelled);
    }

    private async Task FlushProgressInternalAsync(CancellationToken cancellationToken)
    {
        // Serialize all flushes (manual + auto)
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Snapshot and clear buffers under lock
            List<FileRecord> filesToFlush;
            List<DirRecord> dirsToFlush;

            lock (_bufferLock)
            {
                if (_pendingFiles.Count == 0 && _pendingDirs.Count == 0)
                    return;

                filesToFlush = new List<FileRecord>(_pendingFiles);
                dirsToFlush = new List<DirRecord>(_pendingDirs);

                _pendingFiles.Clear();
                _pendingDirs.Clear();
            }

            var delta = new RepoDelta
            {
                ScanSequence = ScanSequence,
                Files = filesToFlush,
                Dirs = dirsToFlush,
                DeletedFiles = new List<FileTombstone>(),
                DeletedDirs = new List<DirTombstone>()
            };

            await _repo.CommitDeltaAsync(delta, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }
}