// DuplicateFileFinderLib/Repository/ScanSession.cs

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed class ScanSession : IScanSession
{
    private readonly Repo _repo;
    private readonly int _maxFilesBeforeFlush;
    private readonly int _maxDirsBeforeFlush;

    private readonly Lock _bufferLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    
    private readonly Dictionary<long, DirRecord> _pendingDirs = new();
    private readonly Dictionary<long, FileRecord> _pendingFiles = new();
    
    private bool _finished;
    
    public ScanSession(
        Repo repo,
        ScanRun run,
        DirRecord rootDir,
        int maxFilesBeforeFlush,
        int maxDirsBeforeFlush)
    {
        _repo = repo;
        Run = run;
        _maxFilesBeforeFlush = maxFilesBeforeFlush;
        _maxDirsBeforeFlush = maxDirsBeforeFlush;
        RootDir = rootDir;
        
        
    }
    
    public ScanRun Run { get; }

    public long ScanSequence => Run.ScanSequence;
    public string RootPath => Run.RootPath;
    // public ScanRoot ScanRoot { get; set; }

    public DirRecord RootDir { get; init; }

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

    public long AddOrUpdateDirectory(DirRecord dir)
    {
        bool shouldFlush = false;
        lock (_bufferLock)
        {
            if (IsNewDir(dir.DirId))
            {
                dir = dir with
                {
                    DirId = _repo.AllocateDirId()
                };
            }
            _pendingDirs[dir.DirId] = dir with {LastSeenScanSequence = ScanSequence};

            if (_pendingDirs.Count >= _maxDirsBeforeFlush)
                shouldFlush = true;
        }

        if (shouldFlush)
            _ = FlushProgressAsync();
        
        return dir.DirId;
    }

    public void AddOrUpdateFile(ref FileRecord file)
    {
        bool shouldFlush = false;

        lock (_bufferLock)
        {
            if (IsNewFile(file.FileId))
            {
                file = file with { FileId = _repo.AllocateFileId() };
            }
            _pendingFiles[file.FileId] = file with {LastSeenScanSequence = ScanSequence};

            if (_pendingFiles.Count >= _maxFilesBeforeFlush)
                shouldFlush = true;
        }
        
        if (shouldFlush)
            _ = FlushProgressAsync();
        
    }

    bool IsNewDir(long dirId) => dirId <= 0;

    bool IsNewFile(long fileId) => fileId <= 0;
    
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

                dirsToFlush = new List<DirRecord>(_pendingDirs.Count);
                filesToFlush = new List<FileRecord>(_pendingFiles.Count);
                
                dirsToFlush.AddRange(_pendingDirs.Values);
                filesToFlush.AddRange(_pendingFiles.Values);

                _pendingDirs.Clear();
                _pendingFiles.Clear();
            }

            var delta = new RepoDelta
            {
                ScanSequence = ScanSequence,
                Dirs         = dirsToFlush,
                Files        = filesToFlush,
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

        _repo.MarkScanCompleted(ScanSequence);
        _finished = true;
    }

    public async Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
    {
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        _repo.MarkScanFailed(ScanSequence, errorMessage, cancelled);
        _finished = true;
    }
}