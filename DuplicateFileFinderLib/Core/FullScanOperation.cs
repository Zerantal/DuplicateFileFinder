using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

using FilesToHashList = System.Collections.Generic.List<DuplicateFileFinderLib.Core.FileToHash<
    DuplicateFileFinderLib.Repository.Core.Scan.FileHashToken>>;

namespace DuplicateFileFinderLib.Core;

internal sealed class FullScanOperation(
    IRepoHost host,
    IFileEnumerator fs,
    IHashingRunner<FileHashToken> hashingRunner,
    IVolumeInfoProvider? volumeInfoProvider)
{
    private readonly IRepoInternal _repo = host.Repo as IRepoInternal ??
                                           throw new InvalidOperationException(
                                               "Repo does not implement IRepoInternal.");

    public Task ExecuteAsync(
        string rootPath,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
        => ExecuteAsync(rootPath, new ScanOptions(), progress, ct);

    public Task ExecuteAsync(
        long scanRootId,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct) =>
        ExecuteAsync(scanRootId, new ScanOptions(), progress, ct);

    public Task ExecuteAsync(
        DirHandle startDir,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct) =>
        ExecuteAsync(startDir, new ScanOptions(), progress, ct);

    // New location scan
    public async Task ExecuteAsync(
        string rootPath,
        ScanOptions options,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        rootPath = PathUtils.NormalizePath(rootPath);

        var vInfo = TryGetVolumeInfo(rootPath);
        ConfigureHashingRunner(vInfo);

        ScanContext ctx;
        using (PhaseScope.Begin(ScanPhase.Preparing))
        using (TimingLog.StartPhase(ScanPhase.Preparing))
        {
            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Preparing,
                $"Preparing location scan for {rootPath}...",
                indeterminate: true);
            ctx = await _repo.BeginNewScanAsync(rootPath, options, vInfo, ct).ConfigureAwait(false);
        }
        await ExecuteWithContextAsync(ctx, progress, ct, startPending: null).ConfigureAwait(false);
    }

    // Location re-scanning
    private async Task ExecuteAsync(
        long scanRootId,
        ScanOptions options,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        var scanRoot = _repo.ScanRootsView.FirstOrDefault(r => r.RootId == scanRootId)
                       ?? throw new KeyNotFoundException($"ScanRootId not found: {scanRootId}");

        // Use the scan root's last-known mount point to probe the currently mounted volume.
        var probePath = ResolveScanRootPath(scanRoot);

        var vInfo = TryGetVolumeInfo(probePath);
        ConfigureHashingRunner(vInfo);

        ScanContext ctx;
        using (PhaseScope.Begin(ScanPhase.Preparing))
        using (TimingLog.StartPhase(ScanPhase.Preparing))
        {
            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Preparing,
                $"Preparing location re-scan for {probePath}...",
                indeterminate: true);
            ctx = await _repo.BeginRescanAsync(scanRootId, options, vInfo, ct).ConfigureAwait(false);
        }
        await ExecuteWithContextAsync(ctx, progress, ct, startPending: null).ConfigureAwait(false);
    }

    // Folder re-scanning
    public async Task ExecuteAsync(
        DirHandle startDir,
        ScanOptions options,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        if (!startDir.IsValid)
            throw new ArgumentException("DirHandle is not valid", nameof(startDir));

        // Subtree rescans must start fresh (per requirements).
        options = options with { StartFresh = true };

        var snap = _repo.TryGetScanRootView(startDir.ScanRootId);
        if (snap is null)
            throw new InvalidOperationException(
                $"Cannot rescan folder because no snapshot is loaded for ScanRootId {startDir.ScanRootId}.");

        var scanRoot = _repo.ScanRootsView.FirstOrDefault(r => r.RootId == startDir.ScanRootId)
                       ?? throw new KeyNotFoundException($"ScanRootId not found: {startDir.ScanRootId}");

        var probePath = ResolveScanRootPath(scanRoot);
        var vInfo = TryGetVolumeInfo(probePath);
        ConfigureHashingRunner(vInfo);

        ScanContext ctx;
        using (PhaseScope.Begin(ScanPhase.Preparing))
        using (TimingLog.StartPhase(ScanPhase.Preparing))
        {
            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Preparing,
                $"Preparing folder re-scan for {probePath}...",
                indeterminate: true);
            ctx = await _repo.BeginSubtreeScanAsync(startDir.ScanRootId, options, vInfo, ct).ConfigureAwait(false);
        }

        // Compute the starting directory path from the loaded snapshot.
        var (dirId, fullPath) = ResolveStartDir(ctx.Run.RootPath, snap, startDir.Index);
        var startPending = new PendingDir(dirId, fullPath);

        await ExecuteWithContextAsync(ctx, progress, ct, startPending).ConfigureAwait(false);
    }

    private async Task ExecuteWithContextAsync(
        ScanContext ctx,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct,
        PendingDir? startPending)
    {
        var session = ctx.Session;
        var rootPath = ctx.Run.RootPath;
        var frontier = ScanFrontier.Create(rootPath, session.RootDirCursor, ctx.Checkpoint, startPending);

        // Let the session decide when to flush; it can query the frontier only when needed.
        session.SetPendingDirsProvider(frontier.Snapshot);

        try
        {
            //TODO simplify if statement?
            if (startPending is not null)
            {
                if (!Directory.Exists(startPending.Value.FullPath))
                    throw new DirectoryNotFoundException(
                        $"Folder rescan path does not exist: {startPending.Value.FullPath}");
            }
            else
            {
                if (!Directory.Exists(rootPath))
                    throw new DirectoryNotFoundException($"Root scan path does not exist: {rootPath}");
            }

            FilesToHashList filesToHash;

            using (PhaseScope.Begin(ScanPhase.Enumerating))
            using (TimingLog.StartPhase(ScanPhase.Enumerating))
            {
                filesToHash = await EnumerateAsync(session, frontier, progress, ct).ConfigureAwait(false);
            }

            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {
                await hashingRunner.HashFilesAsync(
                    filesToHash,
                    progress,
                    (token, bytes, errorMsg) =>
                    {
                        session.OnFileHashCompleted(token, bytes, errorMsg);
                        TimingLog.Counter("hashed_files");
                        TimingLog.Counter("bytes_hashed", token.Size);
                    },
                    ct).ConfigureAwait(false);
            }

            await session.CompleteAsync(ct).ConfigureAwait(false);

            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Completed,
                "Finished scanning",
                1.0,
                running: false);
        }
        catch (OperationCanceledException)
        {
            await session.FailAsync("Scan cancelled.", true, ct).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await session.FailAsync(ex.Message, false, ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private async Task<FilesToHashList> EnumerateAsync(
        IScanSession session,
        ScanFrontier frontier,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        var filesToHash = new FilesToHashList(256 * 1024);

        long dirsVisited = 0;

        while (frontier.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var next = frontier.Pop();

            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Enumerating,
                $"Scanning {next.FullPath}",
                indeterminate: true,
                processed: dirsVisited++);

            await EnumerateFolderOnce(
                    next.FullPath,
                    new DirCursor(next.DirId),
                    session,
                    filesToHash,
                    frontier,
                    ct)
                .ConfigureAwait(false);

            if ((dirsVisited & 0xFF) == 0)
                await Task.Yield();
        }

        return filesToHash;
    }

    private Task EnumerateFolderOnce(
        string folderPath,
        DirCursor parent,
        IScanSession session,
        FilesToHashList filesToHash,
        ScanFrontier frontier,
        CancellationToken ct)
    {
        var normDir = PathUtils.NormalizePath(folderPath);

        TimingLog.Counter("folders");

        var ctx = session.BeginDirectory(parent);

        foreach (var e in fs.EnumerateChildren(normDir, ct))
        {
            ct.ThrowIfCancellationRequested();

            // Construct child path from normalized parent + name (no per-entry NormalizePath)
            var childPath = PathUtils.JoinNormalized(normDir, e.Name);

            if (e.IsDirectory)
            {
                var observed = new ObservedDir
                {
                    Name = e.Name,
                    CreatedTicks = e.CreationTimeUtc.Ticks,
                    ModifiedTicks = e.ModifiedTimeUtc.Ticks,
                    ErrorMessage = null
                };

                var childCursor = session.OnDirectoryFound(in observed, ref ctx);

                frontier.Push(new PendingDir(childCursor.DirId, childPath));
                continue;
            }

            var observedFile = new ObservedFile
            {
                Name = e.Name,
                Size = e.Length,
                CreatedTicks = e.CreationTimeUtc.Ticks,
                ModifiedTicks = e.ModifiedTimeUtc.Ticks,
                ErrorMessage = null
            };

            var decision = session.OnFileFound(in observedFile, ref ctx);

            if (decision.ShouldHash && observedFile.Size > 0)
            {
                filesToHash.Add(new FileToHash<FileHashToken>(childPath, decision.Token));
            }

            TimingLog.Counter("files");
        }

        session.EndDirectory(ref ctx);
        return Task.CompletedTask;
    }

    private VolumeInfo? TryGetVolumeInfo(string rootPath)
    {
        try
        {
            return volumeInfoProvider?.GetVolumeInfoForPath(rootPath);
        }
        catch
        {
            return null;
        }
    }

    private void ConfigureHashingRunner(VolumeInfo? vInfo)
    {
        var hashDop = vInfo is { IsRotational: true } ? 1 : Environment.ProcessorCount;
        hashingRunner.ReadBufferSize = vInfo is { IsRotational: true } ? 512 * 1024 : 128 * 1024;
        hashingRunner.MaxDegreeOfParallelism = hashDop;
    }

    // TODO: review where these methods belong/how they are implemented. Maybe put them in
    // helper class
    private static string ResolveScanRootPath(ScanRoot scanRoot)
    {
        var p = scanRoot.RootPath;
        if (!string.IsNullOrWhiteSpace(scanRoot.VolumePath) && !Path.IsPathRooted(p))
            p = Path.Combine(scanRoot.VolumePath!, p);

        return PathUtils.NormalizePath(p);
    }

    private static (long dirId, string fullPath) ResolveStartDir(
        string rootPath,
        ScanRootSnapshotView snap,
        int dirIndex)
    {
        if (dirIndex < 0 || dirIndex >= snap.Dirs.Count)
            throw new ArgumentOutOfRangeException(nameof(dirIndex));

        var byId = new Dictionary<long, int>(snap.Dirs.Count);
        for (var i = 0; i < snap.Dirs.Count; i++)
            byId[snap.Dirs[i].DirId] = i;

        var parts = new Stack<string>();
        var cur = snap.Dirs[dirIndex];
        var startDirId = cur.DirId;

        // Build relative path from this dir up to the root (ParentDirId < 0)
        while (cur.ParentDirId >= 0)
        {
            if (cur.NameStrIdx >= 0)
            {
                var name = snap.StringPool.GetString(cur.NameStrIdx);
                if (!string.IsNullOrEmpty(name))
                    parts.Push(name);
            }

            if (!byId.TryGetValue(cur.ParentDirId, out var parentIdx))
                break;

            cur = snap.Dirs[parentIdx];
        }

        var fullPath = rootPath;
        while (parts.Count != 0)
            fullPath = PathUtils.JoinNormalized(fullPath, parts.Pop());

        return (startDirId, PathUtils.NormalizePath(fullPath));
    }
}
