using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core;
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
    private readonly IFileDirReadModel _fileDirIndex = host.FileDirIndex;
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
        var repoView    = _repo.GetRepoSnapshotView();
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
            bool unchangedDir = false;
            if (_fileDirIndex.TryGetDir(existingDirId, out var existingDirHandle))
            {
                var existingDir = repoView.GetDirRecord(existingDirHandle);
                unchangedDir =
                    skipUnchangedDirectories &&
                    existingDir.ModifiedTicks == dirEntry.ModifiedTimeUtc.Ticks &&
                    existingDir.Status != ScanEntryStatus.Deleted;
            }

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
        RepoSnapshotView repoView,
        CancellationToken token = default)
    {
        var normDir = PathUtils.NormalizePath(location);
        
        TimingLog.Counter("folders");
        _fileDirIndex.TryGetDir(parentDirId, out var parentDir);
        
        var expectedDirs  = _treeIndex.GetChildDirs(parentDir)
            .Select(h => (Handle: h, Record: repoView.GetDirRecord(h)))
            .ToDictionary(tuple => repoView.DecodeDirName(tuple.Handle), PathUtils.PathComparer);
        var expectedFiles = _treeIndex.GetChildFiles(parentDir)
            .Select(h => (Handle: h, Record: repoView.GetFileRecord(h)))
            .ToDictionary(tuple => repoView.DecodeFileName(tuple.Handle), PathUtils.PathComparer);
        
        foreach (var e in fs.EnumerateChildren(normDir, token))
        {
            if (e.IsDirectory)
            {
                long existingDirId = 0;
                if (expectedDirs.TryGetValue(e.Name, out var dir))
                {
                    existingDirId = dir.Record.DirId;
                    expectedDirs.Remove(e.Name);
                }
        
                // Defer whether to recurse; EnumerateQuickAsync will decide based on metadata comparison.
                dirsToVisit.Push((e, parentDirId, existingDirId));
                continue;
            }
        
            var fullPath = PathUtils.NormalizePath(e.FullPath);
        
            long existingFileId = 0;
            bool unchangedFile = false;
            FileRecordV2? existingFile = null;
            if (expectedFiles.TryGetValue(e.Name, out var file))
            {
                existingFileId = file.Record.FileId;
                expectedFiles.Remove(e.Name);
                
                existingFile = existingFileId != 0 ? repoView.GetFileRecord(file.Handle) : null;
            
                unchangedFile =
                    existingFile is not null &&
                    existingFile.Value.Size     == e.Length &&
                    existingFile.Value.ModifiedTicks == e.ModifiedTimeUtc.Ticks &&
                    existingFile.Value.Hash     != HashKey.NotComputed &&
                    existingFile.Value.Status   != ScanEntryStatus.Deleted;
            }
            
        
            var updatedFile = new FileRecord
            {
                FileId   = existingFileId,
                DirId    = parentDirId,
                Name     = e.Name,
                Size     = e.Length,
                Created  = e.CreationTimeUtc,
                Modified = e.ModifiedTimeUtc,
                Status   = unchangedFile ? existingFile!.Value.Status : ScanEntryStatus.Enumerated ,
                Hash     = unchangedFile ? existingFile!.Value.Hash : HashKey.NotComputed
            };
        
            session.AddOrUpdateFile(ref updatedFile);
        
            // Only queue for hashing if the file is new, has changed, or has no previous hash.
            if (!unchangedFile && e.Length > 0)
            {
                filesToHash.Add(new HashingRunner.FileToHash(fullPath, updatedFile));
            }
        
            TimingLog.Counter("files");
        }
        
        // Note: won't purge files/dirs if dir not enumerated
        Dff.PurgeOldDirs(session, _treeIndex, _fileDirIndex, repoView, expectedDirs.Values.Select(t => t.Record.DirId).ToArray());
        Dff.PurgeOldFiles(session, expectedFiles.Values.Select(t => t.Record.FileId).ToArray());
        
        return Task.CompletedTask;
    }
}
