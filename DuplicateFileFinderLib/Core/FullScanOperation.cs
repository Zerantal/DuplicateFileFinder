using System.Collections;
using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Core;

internal class FullScanOperation(
    IRepoHost host,
    IFileEnumerator fs,
    IChecksumPipeline pipeline,
    IVolumeInfoProvider? volumeInfoProvider)
{
    private readonly IRepo _repo = host.Repo;
    private readonly ITreeIndexReadModel _treeIndex = host.TreeIndex;
    private int _hashDegreeOfParallelism;

    public async Task ExecuteAsync(string rootPath, IProgress<DuplicateFileFinderProgressReport>? progress, CancellationToken ct)
    {
        rootPath = PathUtils.NormalizePath(rootPath);

        // Volume info
        VolumeInfo? vInfo = null;
        try { vInfo = volumeInfoProvider?.GetVolumeInfoForPath(rootPath); } catch { /* ignore */ }

        _hashDegreeOfParallelism = vInfo is { IsRotational: true } ? 1 : Environment.ProcessorCount;
      
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
        var repoView = _repo.GetRepoView();
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
                // Try to reuse existing DirId from tree index
                long existingDirId = 0;
                if (expectedDirs.TryGetValue(e.Name, out var id))
                {
                    existingDirId = id;
                    expectedDirs.Remove(e.Name);
                }

                // Push child dir, carrying both parent and existing id (if any)
                dirsToVisit.Push((e, parentDirId, existingDirId));
                continue;
            }

            var fullPath = PathUtils.NormalizePath(e.FullPath);

            // Try to reuse existing FileId from tree index
            long existingFileId = 0;
            if (expectedFiles.TryGetValue(e.Name, out var fileId))
            {
                existingFileId = fileId;
                expectedFiles.Remove(e.Name);
            }

            // Normal path: record as enumerated, hash not computed yet.
            FileRecord file = new FileRecord
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

            session.AddOrUpdateFile(ref file);

            // Only non-zero files are hashed
            if (e.Length > 0)
            {
                filesToHash.Add(new HashingRunner.FileToHash(
                    fullPath,
                    file));
            }

            TimingLog.Counter("files");
        }
         
        PurgeOldDirs(session, expectedDirs.Values);
        PurgeOldFiles(session, expectedFiles.Values);

        return Task.CompletedTask;
    }

    private void PurgeOldDirs(IScanSession session, IEnumerable<long> dirsToRemove)
    {
        foreach (var dirId in dirsToRemove)
        {
            var subDirs = _treeIndex.GetChildDirIds(dirId);
            var files = _treeIndex.GetChildFileIds(dirId);
             
            PurgeOldDirs(session, subDirs);
            PurgeOldFiles(session, files);

            var dirRecord = new DirRecord
            {
                DirId = dirId,
                Status = ScanEntryStatus.Deleted
            };
            session.AddOrUpdateDirectory(dirRecord);
        }
    }

    private void PurgeOldFiles(IScanSession session, IEnumerable<long> filesToRemove)
    {
        foreach (var fileId in filesToRemove)
        {
            FileRecord file = new FileRecord
            {
                FileId = fileId,
                Status = ScanEntryStatus.Deleted
            };
            session.AddOrUpdateFile(ref file);
        }
    }
}