// DuplicateFileFinderLib/Repository/Core/ScanSession.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

internal sealed class ScanSession : IScanSession
{
    private readonly IRepoInternal _repo;

    private readonly BaselineIndex _baseline;
    private readonly DirectoryComparator _cmp;
    private readonly MutationBuffer _mut;

    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Task? _inflightFlush;
    private bool _finished;

    private ScanRun Run { get; }
    private long ScanSequence => Run.ScanSequence;
    private long ScanRootId => Run.ScanRootId;

    public DirCursor RootDirCursor { get; }
    
    private long _lastCheckpointUtcTicks;
    private readonly TimeSpan _minCheckpointInterval;
    private int _hasUncheckpointedChanges;
    
    private Func<PendingDir[]>? _getPendingDirs;

    // Deterministic time source for tests (defaults to DateTime.UtcNow.Ticks)
    private readonly Func<long> _utcNowTicks;

    public ScanSession(
        IRepoInternal repo,
        ScanRun run,
        DirScanInput rootDirInput,
        TimeSpan? minCheckpointInterval = null,
        Func<long>? utcNowTicks = null)
    {
        _repo = repo;
        Run = run;
        
        _minCheckpointInterval = minCheckpointInterval ?? TimeSpan.FromSeconds(300);
        _utcNowTicks = utcNowTicks ?? (static () => DateTime.UtcNow.Ticks);
        _lastCheckpointUtcTicks = _utcNowTicks();

        var baselineView = _repo.TryGetScanRootView(run.ScanRootId);

        _baseline = new BaselineIndex(baselineView);
        _cmp = new DirectoryComparator(_baseline);
        _mut = new MutationBuffer(_repo, scanSequence: run.ScanSequence);

        // Root cursor should be stable (repo ScanRoot.DirId). If missing, allocate.
        var rootDirId = rootDirInput.DirId > 0 ? rootDirInput.DirId : _repo.AllocateDirId();
        RootDirCursor = new DirCursor(rootDirId);

        // Seed root record as "known, not yet enumerated"
        var rootInternal = rootDirInput with { DirId = rootDirId, ParentDirId = -1, Name = "", Status = ScanEntryStatus.None };
        _mut.UpsertDir(rootInternal);
    }

    public void SetPendingDirsProvider(Func<PendingDir[]> getPendingDirs)
    {
        _getPendingDirs = getPendingDirs ?? throw new ArgumentNullException(nameof(getPendingDirs));
    }

    public async ValueTask DisposeAsync()
    {
        // If disposed without completion/failure, mark failed.
        if (!_finished)
            await _repo.MarkScanFailedAsync(ScanSequence, 
                "ScanSession disposed before completion.", true);

        await Task.CompletedTask;
    }

    // -------- Resume support --------

    public void ImportPartialSnapshot(in ScanRootSnapshotV2 partial)
    {
        // Seed FULL state for final BuildSnapshotV2.
        // This relies on PackedStringPool.GetString(), which your tests use.
        lock (_mut.Sync)
        {
            foreach (var d in partial.Dirs)
            {
                var name = partial.StringPool.GetString(d.NameStrIdx);
                var err = d.ErrorMessageStrIdx >= 0 ? partial.StringPool.GetString(d.ErrorMessageStrIdx) : null;

                _mut.UpsertDir(new DirScanInput
                {
                    DirId = d.DirId,
                    ParentDirId = d.ParentDirId,
                    Name = name,
                    CreatedTicks = d.CreatedTicks,
                    ModifiedTicks = d.ModifiedTicks,
                    Status = d.Status,
                    ErrorMessage = err
                });
            }

            foreach (var f in partial.Files)
            {
                var name = partial.StringPool.GetString(f.NameStrIdx);
                var err = f.ErrorMessageStrIdx >= 0 ? partial.StringPool.GetString(f.ErrorMessageStrIdx) : null;

                _mut.UpsertFile(new FileScanInput
                {
                    FileId = f.FileId,
                    DirId = f.DirId,
                    Name = name,
                    Size = f.Size,
                    Hash = f.Hash,
                    CreatedTicks = f.CreatedTicks,
                    ModifiedTicks = f.ModifiedTicks,
                    Status = f.Status,
                    ErrorMessage = err
                });
            }
        }
    }

    // -------- Enumeration callbacks --------

    public DirEnumerationContext BeginDirectory(DirCursor parentDirId)
    {
        // Ensure root is set to enumerated when we actually enumerate it.
        if (parentDirId.DirId == RootDirCursor.DirId)
        {
            _mut.UpsertDir(new DirScanInput
            {
                DirId = RootDirCursor.DirId,
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            });

            Volatile.Write(ref _hasUncheckpointedChanges, 1);
            TryScheduleCheckpointFlush();
        }

        return _cmp.Begin(parentDirId);
    }

    public DirCursor OnDirectoryFound(in ObservedDir dir, ref DirEnumerationContext ctx)
    {
        var existingId = _cmp.TryConsumeExpectedDirId(ref ctx, dir.Name);

        var input = new DirScanInput
        {
            DirId = existingId > 0 ? existingId : -1,
            ParentDirId = ctx.ParentDirId,
            Name = dir.Name,
            CreatedTicks = dir.CreatedTicks,
            ModifiedTicks = dir.ModifiedTicks,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = dir.ErrorMessage
        };

        var childId = _mut.UpsertDir(input);
        
        Volatile.Write(ref _hasUncheckpointedChanges, 1);
        TryScheduleCheckpointFlush();

        return new DirCursor(childId);
    }
    
    public FileHashDecision OnFileFound(in ObservedFile file, ref DirEnumerationContext ctx)
    {
        var existingId = _cmp.TryConsumeExpectedFileId(ref ctx, file.Name);

        var internalInput = new FileScanInput
        {
            FileId = existingId > 0 ? existingId : -1,
            DirId = ctx.ParentDirId,
            Name = file.Name,
            Size = file.Size,
            Hash = HashKey.NotComputed,
            CreatedTicks = file.CreatedTicks,
            ModifiedTicks = file.ModifiedTicks,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = file.ErrorMessage
        };

        // Decide if hashing is required.
        var shouldHash = HashPolicy.ShouldHash(internalInput, _baseline, Run.HashPolicy);

        // If not hashing and baseline has a valid hash, reuse it now.
        if (!shouldHash && internalInput.FileId > 0 &&
            _baseline.TryGetBaselineFile(internalInput.FileId, out var old) &&
            old.Hash != HashKey.NotComputed)
        {
            internalInput = internalInput with { Hash = old.Hash };
        }

        _mut.UpsertFile(internalInput);

        var decision = shouldHash
            ? new FileHashDecision(true,
                new FileHashToken(internalInput.DirId, internalInput.Name, internalInput.Size))
            : FileHashDecision.NoHash;

        Volatile.Write(ref _hasUncheckpointedChanges, 1);
        TryScheduleCheckpointFlush();

        return decision;
    }
    
    public void OnFileHashCompleted(in FileHashToken token, ReadOnlyMemory<byte> hashBytes, string? errorMessage)
    {
        // Exactly one should be non-null.
        if (errorMessage is null)
        {
            var hk = new HashKey(hashBytes);
            _mut.ApplyFileHash(token.DirId, token.Name, hk);

            Volatile.Write(ref _hasUncheckpointedChanges, 1);
            TryScheduleCheckpointFlush();
            return;
        }

        _mut.ApplyFileError(token.DirId, token.Name, errorMessage);

        Volatile.Write(ref _hasUncheckpointedChanges, 1);
        TryScheduleCheckpointFlush();
    }
    
    public void EndDirectory(ref DirEnumerationContext ctx)
    {
        // Anything expected but not seen => deleted
        foreach (var delFile in _cmp.ConsumeRemainingExpectedFiles(ref ctx))
        {
            var f = new FileScanInput
            {
                FileId = delFile.id,
                DirId = ctx.ParentDirId,
                Name = delFile.name,
                Size = 0,
                Hash = HashKey.NotComputed,
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.Deleted,
                ErrorMessage = null
            };

            _mut.UpsertFile(f);
        }

        foreach (var delDir in _cmp.ConsumeRemainingExpectedDirs(ref ctx))
        {
            var d = new DirScanInput
            {
                DirId = delDir.id,
                ParentDirId = ctx.ParentDirId,
                Name = delDir.name,
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.Deleted,
                ErrorMessage = null
            };

            _mut.UpsertDir(d);
        }

        _cmp.Clear(ref ctx);
        
        Volatile.Write(ref _hasUncheckpointedChanges, 1);
        TryScheduleCheckpointFlush();
    }

    private void TryScheduleCheckpointFlush()
    {
        if (_finished)
            return;

        if (Volatile.Read(ref _hasUncheckpointedChanges) == 0)
            return;

        var now = _utcNowTicks();
        var last = Interlocked.Read(ref _lastCheckpointUtcTicks);

        // Too soon => skip (time-based policy only).
        if (last != 0 && now - last < _minCheckpointInterval.Ticks)
            return;

        // Already flushing => skip
        var t = _inflightFlush;
        if (t is { IsCompleted: false })
            return;

        // Schedule a flush task; do not await (hot path).
        _inflightFlush = FlushProgressAsync(CancellationToken.None);
    }

    private PendingDir[] GetPendingDirsForCheckpoint()
    {
        try
        {
            return _getPendingDirs?.Invoke() ?? Array.Empty<PendingDir>();
        }
        catch
        {
            return Array.Empty<PendingDir>();
        }
    }

    internal async Task FlushProgressAsync(CancellationToken ct)
    {
        if (_finished)
            return;

        if (Volatile.Read(ref _hasUncheckpointedChanges) == 0)
            return;

        var now = _utcNowTicks();
        var last = Interlocked.Read(ref _lastCheckpointUtcTicks);

        // Too soon => skip
        if (last != 0 && now - last < _minCheckpointInterval.Ticks)
            return;

        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_finished)
                return;

            if (Volatile.Read(ref _hasUncheckpointedChanges) == 0)
                return;

            now = _utcNowTicks();
            last = Interlocked.Read(ref _lastCheckpointUtcTicks);

            if (last != 0 && now - last < _minCheckpointInterval.Ticks)
                return;

            ScanRootSnapshotV2 partial;
            lock (_mut.Sync)
            {
                partial = _mut.DrainCheckpointSnapshot(ScanRootId);
            }

            if (partial.Dirs.Length == 0 && partial.Files.Length == 0)
            {
                Volatile.Write(ref _hasUncheckpointedChanges, 0);
                Interlocked.Exchange(ref _lastCheckpointUtcTicks, now);
                return;
            }

            var checkpoint = new ScanCheckpoint
            {
                CheckpointVersion = ScanCheckpoint.CurrentCheckpointVersion,
                ScanRootId = ScanRootId,
                ScanSequence = Run.ScanSequence,
                RootPath = Run.RootPath,            
                PendingDirs = GetPendingDirsForCheckpoint(),
                PartialSnapshot = partial,
                CreatedAtUtcTicks = now
            };

            await _repo.CommitCheckpoint(checkpoint, ct).ConfigureAwait(false);
        
            // Mark success
            Volatile.Write(ref _hasUncheckpointedChanges, 0);
            Interlocked.Exchange(ref _lastCheckpointUtcTicks, now);
        }
        finally
        {
            _flushGate.Release();
        }
    }
        
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        // Build V2 snapshot (dirs/files arrays + PackedStringPool)
        var snapshot = _mut.BuildSnapshotV2(ScanRootId);
        await _repo.CommitScanRootSnapshotV2Async(snapshot, cancellationToken).ConfigureAwait(false);

        await _repo.MarkScanCompletedAsync(ScanSequence, cancellationToken).ConfigureAwait(false);
        _finished = true;

        // Successful completion => checkpoint is no longer needed.
        try
        {
            await _repo.DeleteScanCheckpointAsync(ScanRootId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort.
        }
    }

    public async Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
    {
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        await _repo.MarkScanFailedAsync(ScanSequence, errorMessage, cancelled, cancellationToken).ConfigureAwait(false);
        _finished = true;

    // Intentionally keep checkpoint(s) on failure/cancel for resume.
    }
}