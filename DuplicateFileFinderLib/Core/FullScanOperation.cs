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
    {
        return ExecuteAsync(rootPath, new ScanOptions(), progress, ct);
    }

    public async Task ExecuteAsync(
        string rootPath,
        ScanOptions options,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        rootPath = PathUtils.NormalizePath(rootPath);

        VolumeInfo? vInfo = null;
        try
        {
            vInfo = volumeInfoProvider?.GetVolumeInfoForPath(rootPath);
        }
        catch
        {
            /* ignore */
        }

        // Configure hashing runnerb.Repository.Core.Scan.FileHashToken>>;
        var hashDop = vInfo is { IsRotational: true } ? 1 : Environment.ProcessorCount;
        hashingRunner.ReadBufferSize = vInfo is { IsRotational: true } ? 512 * 1024 : 128 * 1024;
        hashingRunner.MaxDegreeOfParallelism = hashDop;

        // Scan context (explicit restart vs resume behavior)
        var ctx = await _repo.BeginScanAsync(rootPath, options, vInfo, ct);
        var session = ctx.Session;
        var frontier = ScanFrontier.Create(rootPath, session.RootDirCursor, ctx.Checkpoint);

        // Let the session decide when to flush; it can query the frontier only when needed.
        session.SetPendingDirsProvider(frontier.Snapshot);

        try
        {
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Root scan path does not exist: {rootPath}");

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
                frontier.Push(new PendingDir(childCursor.DirId, PathUtils.NormalizePath(e.FullPath)));
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
                filesToHash.Add(new FileToHash<FileHashToken>(
                    PathUtils.NormalizePath(e.FullPath),
                    decision.Token));

            TimingLog.Counter("files");
        }

        session.EndDirectory(ref ctx);
        return Task.CompletedTask;
    }
}