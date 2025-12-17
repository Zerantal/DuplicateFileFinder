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

internal class FullScanOperation(
    IRepoHost host,
    IFileEnumerator fs,
    IChecksumPipeline pipeline,
    IVolumeInfoProvider? volumeInfoProvider)
{
    private readonly IRepo _repo = host.Repo;
    private readonly ITreeIndexReadModel _treeIndex = host.TreeIndex;
    private readonly IFileDirReadModel _fileDirIndex = host.FileDirIndex;
    private int _hashDegreeOfParallelism;

    public async Task ExecuteAsync(string rootPath, IProgress<DuplicateFileFinderProgressReport>? progress, CancellationToken ct)
    {
        rootPath = PathUtils.NormalizePath(rootPath);

        // Volume info
        VolumeInfo? vInfo = null;
        try { vInfo = volumeInfoProvider?.GetVolumeInfoForPath(rootPath); } catch { /* ignore */ }

        _hashDegreeOfParallelism = vInfo is { IsRotational: true } ? 1 : Environment.ProcessorCount;

        var bufferSize = vInfo is { IsRotational: true } ? 512 * 1024 : 128 * 1024;
        pipeline.BufferSize = bufferSize;      
        
        var session = _repo.BeginScan(rootPath, ScanOperation.FullScan, vInfo);

        try
        {
            if (!Directory.Exists(rootPath))
            {
                string msg = $"Root scan path does not exist: {rootPath}";
                throw new DirectoryNotFoundException(msg);
            }
             
            // 1) Enumerate filesystem and record into repo
            List<HashingRunner.FileToHash> filesToHash;
             
            using (PhaseScope.Begin(ScanPhase.Enumerating))
            using (TimingLog.StartPhase(ScanPhase.Enumerating))
            {
                filesToHash = await EnumerateFullAsync(rootPath, progress, session, ct);
            }

            // 2) Hash all non-zero files that actually need hashing
            using (PhaseScope.Begin(ScanPhase.Hashing))
            using (TimingLog.StartPhase(ScanPhase.Hashing))
            {
                await HashingRunner.RunAsync(filesToHash, session, pipeline, _hashDegreeOfParallelism, progress, ct);
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
    
    private async Task<List<HashingRunner.FileToHash>> EnumerateFullAsync(
        string location, 
        IProgress<DuplicateFileFinderProgressReport>? progress, 
        IScanSession session, 
        CancellationToken token)
    {
        var repoView = _repo.GetRepoSnapshotView();
        var filesToHash   = new List<HashingRunner.FileToHash>();
         
        var dirsToVisit = new Stack<(FsEntry dirEntry, long parentDirId, long existingDirId)>();
        long dirsVisited = 0;

        var rootDir = session.RootDir;
        location = PathUtils.NormalizePath(location);

        // update root folder
        var rootDirId = session.AddOrUpdateDirectory(rootDir with { Status = ScanEntryStatus.Enumerated });

        dirsVisited++;
        DuplicateFileFinderHelpers.Report(
            progress,
            ScanPhase.Enumerating,
            $"Scanning {location}",
            indeterminate: true,
            processed: dirsVisited);
         
        await ScanFolder(location, rootDirId, session, filesToHash, dirsToVisit, repoView, token);

        // recursive scan
        while (dirsToVisit.Count > 0)
        {
            token.ThrowIfCancellationRequested();
             
            var (dirEntry, parentDirId, existingDirId) = dirsToVisit.Pop();

            var dirRecord = new DirRecord
            {
                DirId        = existingDirId,
                ParentDirId  = parentDirId,
                Name = dirEntry.Name,
                Created = dirEntry.CreationTimeUtc,
                Modified = dirEntry.ModifiedTimeUtc,
                Status = ScanEntryStatus.Enumerated
            };

            var newParentId = session.AddOrUpdateDirectory(dirRecord);
             
            dirsVisited++;
            DuplicateFileFinderHelpers.Report(
                progress,
                ScanPhase.Enumerating,
                $"Scanning {dirEntry.FullPath}",
                indeterminate: true,
                processed: dirsVisited);
             
            await ScanFolder(dirEntry.FullPath, newParentId, session, filesToHash, dirsToVisit, repoView, token );
             
            // Give the scheduler a chance occasionally in large trees
            if ((dirsVisited & 0xFF) == 0)
                await Task.Yield();
        }

        return filesToHash;
    }

    /// <summary>
    /// Scan files and directories in single directory (non-recursive)
    /// </summary>
    /// <param name="location"></param>
    /// <param name="parentDirId"></param>
    /// <param name="session"></param>
    /// <param name="filesToHash"></param>
    /// <param name="dirsToVisit"></param>
    /// <param name="repoView"></param>
    /// <param name="token"></param>
    private Task ScanFolder(
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
                // Try to reuse existing DirId from tree index
                long existingDirId = 0;
                if (expectedDirs.TryGetValue(e.Name, out var dir))
                {
                    existingDirId = dir.Record.DirId;
                    expectedDirs.Remove(e.Name);
                }

                // Push child dir, carrying both parent and existing id (if any)
                dirsToVisit.Push((e, parentDirId, existingDirId));
                continue;
            }

            var fullPath = PathUtils.NormalizePath(e.FullPath);

            // Try to reuse existing FileId from tree index
            long existingFileId = 0;
            if (expectedFiles.TryGetValue(e.Name, out var file))
            {
                existingFileId = file.Record.FileId;
                expectedFiles.Remove(e.Name);
            }

            // Normal path: record as enumerated, hash not computed yet.
            FileRecord updatedFile = new FileRecord
            {
                FileId   = existingFileId,
                DirId = parentDirId,
                Name = e.Name,
                Size = e.Length,
                Created  = e.CreationTimeUtc,
                Modified = e.ModifiedTimeUtc,
                Status = ScanEntryStatus.Enumerated,
                Hash = HashKey.NotComputed
            };

            session.AddOrUpdateFile(ref updatedFile);

            // Only non-zero files are hashed
            if (e.Length > 0)
            {
                filesToHash.Add(new HashingRunner.FileToHash(
                    fullPath,
                    updatedFile));
            }

            TimingLog.Counter("files");
        }
         
        Dff.PurgeOldDirs(session, _treeIndex, expectedDirs.Values.Select(t => t.Record.DirId));
        Dff.PurgeOldFiles(session, expectedFiles.Values.Select(t => t.Record.FileId));

        return Task.CompletedTask;
    }
}