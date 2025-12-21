using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

using FilesToHashList = System.Collections.Generic.List<DuplicateFileFinderLib.Core.FileToHash<DuplicateFileFinderLib.Repository.Core.Scan.FileHashToken>>;

namespace DuplicateFileFinderLib.Core;

internal sealed class FullScanOperation(
    IRepoHost host,
    IFileEnumerator fs,
    IHashingRunner<FileHashToken> hashingRunner,
    IVolumeInfoProvider? volumeInfoProvider)
{
    private readonly IRepo _repo = host.Repo;

    public async Task ExecuteAsync(string rootPath, IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        rootPath = PathUtils.NormalizePath(rootPath);

        // Volume info
        VolumeInfo? vInfo = null;
        try { vInfo = volumeInfoProvider?.GetVolumeInfoForPath(rootPath); } catch { /* ignore */ }

        var hashDop = vInfo is { IsRotational: true } ? 1 : Environment.ProcessorCount;
        hashingRunner.ReadBufferSize = vInfo is { IsRotational: true } ? 512 * 1024 : 128 * 1024;
        hashingRunner.MaxDegreeOfParallelism = hashDop;

        var session = _repo.BeginScan(rootPath, ScanOperation.FullScan, vInfo);

        try
        {
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Root scan path does not exist: {rootPath}");

            FilesToHashList filesToHash;

            using (PhaseScope.Begin(ScanPhase.Enumerating))
            using (TimingLog.StartPhase(ScanPhase.Enumerating))
            {
                filesToHash = await EnumerateAsync(rootPath, session, progress, ct).ConfigureAwait(false);
            }

            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {

                await hashingRunner.HashFilesAsync(filesToHash, progress, onFileHashed: (token, bytes, errorMsg) =>
                {
                      session.OnFileHashCompleted(token, bytes, errorMsg);          
                }, ct);
            }

            await session.CompleteAsync(ct).ConfigureAwait(false);

            DuplicateFileFinderHelpers.Report(progress, ScanPhase.Completed, "Finished scanning", 1.0, running: false);
            await _repo.CompactAsync(ct: ct).ConfigureAwait(false);
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
        string rootPath,
        IScanSession session,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        var filesToHash = new FilesToHashList(256 * 1024);
        var stack = new Stack<(FsEntry dirEntry, DirCursor cursor)>();
        
        var rootCursor = session.RootDirCursor;

        // Enumerate root folder and push subdirs
        await EnumerateFolderOnce(rootPath, rootCursor, session, filesToHash, stack, ct).ConfigureAwait(false);

        long dirsVisited = 1;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (dirEntry, cursor) = stack.Pop();

            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Enumerating,
                $"Scanning {dirEntry.FullPath}",
                indeterminate: true,
                processed: dirsVisited++);

            await EnumerateFolderOnce(dirEntry.FullPath, cursor, session, filesToHash, stack, ct).ConfigureAwait(false);

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
        Stack<(FsEntry dirEntry, DirCursor cursor)> stack,
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
                stack.Push((e, childCursor));
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
                filesToHash.Add(new FileToHash<FileHashToken>(
                    FullPath: PathUtils.NormalizePath(e.FullPath),
                    Token: decision.Token));
            }

            TimingLog.Counter("files");
        }

        session.EndDirectory(ref ctx);
        return Task.CompletedTask;
    }
}