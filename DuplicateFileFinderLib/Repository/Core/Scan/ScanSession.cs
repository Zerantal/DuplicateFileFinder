// DuplicateFileFinderLib/Repository/Core/ScanSession.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;
using ScanRun = DuplicateFileFinderLib.Repository.Storage.Models.ScanRun;

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

    public ScanSession(
        IRepoInternal repo,
        ScanRun run,
        DirScanInput rootDirInput,
        int maxFilesBeforeFlush,
        int maxDirsBeforeFlush)
    {
        _repo = repo;
        Run = run;
        
        // Baseline snapshot for this root (may be null for first scan)
        var baselineView = _repo.TryGetScanRootView(run.ScanRootId);

        _baseline = new BaselineIndex(baselineView);
        _cmp = new DirectoryComparator(_baseline);
        _mut = new MutationBuffer(_repo, scanSequence: run.ScanSequence);

        // Root cursor is opaque to scanner; session chooses stable id
        var rootDirId = rootDirInput.DirId > 0 ? rootDirInput.DirId : _repo.AllocateDirId();
        RootDirCursor = new DirCursor(rootDirId);

        // Ensure root exists in current buffers as "known root, not yet enumerated"
        var rootInternal = rootDirInput with { DirId = rootDirId, ParentDirId = -1, Name = "", Status = ScanEntryStatus.None };
        _mut.UpsertDir(rootInternal);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var t = _inflightFlush;
            if (t is not null)
                await t.ConfigureAwait(false);
        }
        catch
        {
            /* ignore */
        }

        if (!_finished)
            _repo.MarkScanFailed(ScanSequence, "ScanSession disposed before completion.", true);
    }

    // ---------------------- Directory enumeration ----------------------
    public DirEnumerationContext BeginDirectory(DirCursor parentDirId)
    {
        // Mark root as enumerated the moment the scanner starts enumerating it.
        if (parentDirId.DirId == RootDirCursor.DirId)
        {
            var rootEnumerated = new DirScanInput
            {
                DirId = RootDirCursor.DirId,
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            _mut.UpsertDir(rootEnumerated);
        }

        return _cmp.Begin(parentDirId);
    }

    public DirCursor OnDirectoryFound(in ObservedDir dir, ref DirEnumerationContext ctx)
    {
        // Reuse by name if baseline had one.
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
        var shouldHash = HashPolicy.ShouldHash(internalInput, _baseline);

        // If not hashing and baseline has a valid hash, reuse it now.
        if (!shouldHash && internalInput.FileId > 0 &&
            _baseline.TryGetBaselineFile(internalInput.FileId, out var old) &&
            old.Hash != HashKey.NotComputed)
        {
            internalInput = internalInput with { Hash = old.Hash };
        }

        _mut.UpsertFile(internalInput);

        return shouldHash
            ? new FileHashDecision(true, new FileHashToken(internalInput.DirId, internalInput.Name, internalInput.Size, internalInput.CreatedTicks, internalInput.ModifiedTicks))
            : FileHashDecision.NoHash;
    }
    
    public void OnFileHashCompleted(in FileHashToken token, ReadOnlyMemory<byte> hashBytes, string? errorMessage)
    {
        // Exactly one should be non-null.
        if (errorMessage is null)
        {
            var hk = new HashKey(hashBytes);
            _mut.ApplyFileHash(token.DirId, token.Name, hk);
            return;
        }

        _mut.ApplyFileError(token.DirId, token.Name, errorMessage);
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
    }

    // ---------------------- Flush / Complete / Fail ----------------------
    public Task FlushProgressAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Stubbed for now.
        lock (_mut.Sync)
        {
            _inflightFlush ??= Task.CompletedTask;
            return _inflightFlush;
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await FlushProgressAsync(cancellationToken).ConfigureAwait(false);

        // Build V2 snapshot (dirs/files arrays + PackedStringPool)
        var snapshot = _mut.BuildSnapshotV2(ScanRootId);

        await _repo.CommitScanRootSnapshotV2Async(snapshot, cancellationToken).ConfigureAwait(false);

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