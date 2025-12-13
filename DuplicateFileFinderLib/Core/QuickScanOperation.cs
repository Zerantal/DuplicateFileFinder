using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Util;
using Dff = DuplicateFileFinderLib.Core.DuplicateFileFinderHelpers;

namespace DuplicateFileFinderLib.Core;

internal class QuickScanOperation(
    IRepoHost host,
    IFileEnumerator fs,
    IChecksumPipeline checksums,
    IVolumeInfoProvider? volumeInfoProvider,
    bool skipUnchangedDirectories = false)
{
    private readonly IRepo _repo = host.Repo;
    private readonly ITreeIndexReadModel _treeIndex = host.TreeIndex;
    private int _hashDegreeOfParallelism;

    public async Task ExecuteAsync(
        string rootPath,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken ct)
    {
        rootPath = PathUtils.NormalizePath(rootPath);

        // Volume info
        VolumeInfo? vInfo = null;
        try { vInfo = volumeInfoProvider?.GetVolumeInfoForPath(rootPath); } catch { /* ignore */ }

        _hashDegreeOfParallelism = vInfo is { IsRotational: true } ? 1 : Environment.ProcessorCount;

        var session = _repo.BeginScan(rootPath, ScanOperation.QuickScan, vInfo);

        try
        {
            if (!Directory.Exists(rootPath))
            {
                string msg = $"Root scan path does not exist: {rootPath}";
                throw new DirectoryNotFoundException(msg);
            }

            // 1) Enumerate filesystem, but only descend into directories whose metadata has changed.
            List<HashingRunner.FileToHash> filesToHash;
            using (PhaseScope.Begin(ScanPhase.Enumerating))
            using (TimingLog.StartPhase(ScanPhase.Enumerating))
            {
                filesToHash = await EnumerateQuickAsync(rootPath, progress, session, ct).ConfigureAwait(false);
            }

            // 2) Hash only files that are new or whose metadata has changed
            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {
                await HashingRunner.RunAsync(
                        filesToHash,
                        session,
                        checksums,
                        _hashDegreeOfParallelism,
                        progress,
                        ct)
                    .ConfigureAwait(false);
            }

            await session.CompleteAsync(ct).ConfigureAwait(false);

            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Completed,
                "Finished quick scan",
                1.0,
                running: false);

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
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<List<HashingRunner.FileToHash>> EnumerateQuickAsync(
        string location,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        IScanSession session,
        CancellationToken token)
    {
        var repoView    = _repo.GetRepoView();
        var filesToHash = new List<HashingRunner.FileToHash>();

        var dirsToVisit = new Stack<(FsEntry dirEntry, long parentDirId, long existingDirId)>();
        long dirsVisited = 0;

        var rootDir = session.RootDir;
        location = PathUtils.NormalizePath(location);

        // Root directory is always enumerated
        var rootDirId = session.AddOrUpdateDirectory(rootDir with { Status = ScanEntryStatus.Enumerated });

        dirsVisited++;
        DuplicateFileFinderHelpers.Report(
            progress,
            ScanPhase.Enumerating,
            $"Scanning {location}",
            indeterminate: true,
            processed: dirsVisited);

        await ScanFolderQuickAsync(
                location,
                rootDirId,
                session,
                filesToHash,
                dirsToVisit,
                repoView,
                token)
            .ConfigureAwait(false);

        while (dirsToVisit.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var (dirEntry, parentDirId, existingDirId) = dirsToVisit.Pop();

            var existingDir = existingDirId != 0 ? repoView.TryGetDir(existingDirId) : null;

            var dirRecord = new DirRecord
            {
                DirId       = existingDirId,
                ParentDirId = parentDirId,
                Name        = dirEntry.Name,
                Created     = dirEntry.CreationTimeUtc,
                Modified    = dirEntry.ModifiedTimeUtc,
                Status      = ScanEntryStatus.Enumerated
            };

            var newParentId = session.AddOrUpdateDirectory(dirRecord);

            dirsVisited++;
            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Enumerating,
                $"Scanning {dirEntry.FullPath}",
                indeterminate: true,
                processed: dirsVisited);

            // Only descend into this directory if the directory's metadata has changed or it is new.
            var unchangedDir =
                skipUnchangedDirectories &&
                existingDir is not null &&
                existingDir.Modified == dirEntry.ModifiedTimeUtc &&
                existingDir.Status   != ScanEntryStatus.Deleted;

            if (!unchangedDir)
            {
                await ScanFolderQuickAsync(
                        dirEntry.FullPath,
                        newParentId,
                        session,
                        filesToHash,
                        dirsToVisit,
                        repoView,
                        token)
                    .ConfigureAwait(false);
            }

            if ((dirsVisited & 0xFF) == 0)
                await Task.Yield();
        }

        return filesToHash;
    }

    /// <summary>
    /// Scan a single directory for quick scan (non-recursive).
    /// Files whose size/mtime are unchanged keep their existing hash and are not re-queued for hashing.
    /// Directories are queued for potential recursion; whether we actually recurse is decided in EnumerateQuickAsync.
    /// </summary>
    private Task ScanFolderQuickAsync(
        string location,
        long parentDirId,
        IScanSession session,
        List<HashingRunner.FileToHash> filesToHash,
        Stack<(FsEntry dirEntry, long parentDirId, long existingDirId)> dirsToVisit,
        IRepoView repoView,
        CancellationToken token = default)
    {
        var normDir = PathUtils.NormalizePath(location);

        TimingLog.Counter("folders");

        var childDirIds  = _treeIndex.GetChildDirIds(parentDirId);
        var childFileIds = _treeIndex.GetChildFileIds(parentDirId);

        var expectedDirs = new Dictionary<string, long>(PathUtils.PathComparer);
        foreach (var dirId in childDirIds)
        {
            var dir = repoView.TryGetDir(dirId);
            if (dir is not null)
                expectedDirs[dir.Name] = dir.DirId;
        }

        var expectedFiles = new Dictionary<string, long>(PathUtils.PathComparer);
        foreach (var fileId in childFileIds)
        {
            var file = repoView.TryGetFile(fileId);
            if (file is not null)
                expectedFiles[file.Name] = file.FileId;
        }

        foreach (var e in fs.EnumerateChildren(normDir, token))
        {
            if (e.IsDirectory)
            {
                long existingDirId = 0;
                if (expectedDirs.TryGetValue(e.Name, out var dirId))
                {
                    existingDirId = dirId;
                    expectedDirs.Remove(e.Name);
                }

                // Defer whether to recurse; EnumerateQuickAsync will decide based on metadata comparison.
                dirsToVisit.Push((e, parentDirId, existingDirId));
                continue;
            }

            var fullPath = PathUtils.NormalizePath(e.FullPath);

            long existingFileId = 0;
            if (expectedFiles.TryGetValue(e.Name, out var fileId))
            {
                existingFileId = fileId;
                expectedFiles.Remove(e.Name);
            }

            FileRecord? existingFile = existingFileId != 0 ? repoView.TryGetFile(existingFileId) : null;

            var unchangedFile =
                existingFile is not null &&
                existingFile.Size     == e.Length &&
                existingFile.Modified == e.ModifiedTimeUtc &&
                existingFile.Hash     != HashKey.NotComputed &&
                existingFile.Status   != ScanEntryStatus.Deleted;

            var file = new FileRecord
            {
                FileId   = existingFileId,
                DirId    = parentDirId,
                Name     = e.Name,
                Size     = e.Length,
                Created  = e.CreationTimeUtc,
                Modified = e.ModifiedTimeUtc,
                Status   = unchangedFile ? existingFile!.Status : ScanEntryStatus.Enumerated ,
                Hash     = unchangedFile ? existingFile!.Hash : HashKey.NotComputed
            };

            session.AddOrUpdateFile(ref file);

            // Only queue for hashing if the file is new, has changed, or has no previous hash.
            if (!unchangedFile && e.Length > 0)
            {
                filesToHash.Add(new HashingRunner.FileToHash(fullPath, file));
            }

            TimingLog.Counter("files");
        }

        // Note: won't purge files/dirs if dir not enumerated
        Dff.PurgeOldDirs(session, _treeIndex, expectedDirs.Values);
        Dff.PurgeOldFiles(session, expectedFiles.Values);

        return Task.CompletedTask;
    }
}
