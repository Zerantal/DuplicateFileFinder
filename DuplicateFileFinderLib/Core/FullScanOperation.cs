using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Core;

internal class FullScanOperation
{
    private readonly IVolumeInfoProvider? _volumeInfoProvider;
    private readonly IRepo _repo;
    private readonly IFileEnumerator _fs;
    private readonly IChecksumPipeline _pipeline;
    private int _hashDegreeOfParallelism;

    public FullScanOperation(
        IRepoHost host, 
        IFileEnumerator fs, 
        IChecksumPipeline pipeline, 
        IVolumeInfoProvider? volumeInfoProvider)
    {
        _repo               = host.Repo;
        _fs                 = fs;
        _pipeline           = pipeline;
        _volumeInfoProvider = volumeInfoProvider;
        _volumeInfoProvider = volumeInfoProvider;
    }

    public async Task ExecuteAsync(string rootPath, IProgress<DuplicateFileFinderProgressReport>? progress, CancellationToken ct)
    {
        rootPath = PathUtils.NormalizePath(rootPath);

        // Volume info
        VolumeInfo? vInfo = null;
        try { vInfo = _volumeInfoProvider?.GetVolumeInfoForPath(rootPath); } catch { /* ignore */ }

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
                await HashingRunner.RunAsync(filesToHash, session, _pipeline, _hashDegreeOfParallelism, progress, ct);
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
         var filesToHash   = new List<HashingRunner.FileToHash>();
         var dirsToVisit   = new Stack<(string fullPath, string name, long dirId)>();
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
         
         await ScanFolder(location, rootDirId, session, filesToHash, dirsToVisit, token);

         // recursive scan
         while (dirsToVisit.Count > 0)
         {
             token.ThrowIfCancellationRequested();
             
             var (dir, name, dirId) = dirsToVisit.Pop();

             var newParentId = session.AddOrUpdateDirectory(new DirRecord
             {
                 ParentDirId = dirId,
                 Name = name,
                 Status = ScanEntryStatus.Enumerated
             });
             
             dirsVisited++;
             DuplicateFileFinderHelpers.Report(
                 progress,
                 ScanPhase.Enumerating,
                 $"Scanning {dir}",
                 indeterminate: true,
                 processed: dirsVisited);
             
             await ScanFolder(dir, newParentId, session, filesToHash, dirsToVisit, token );
             
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
     /// <param name="token"></param>
     private async Task ScanFolder(
         string location,
         long parentDirId,
         IScanSession session,
         List<HashingRunner.FileToHash> filesToHash,
         Stack<(string fullPath, string name, long dirId)> dirsToVisit,
         CancellationToken token)
     {
         var normDir = PathUtils.NormalizePath(location);

         TimingLog.Counter("folders");

         foreach (var e in _fs.EnumerateChildren(normDir, token))
         {
             if (e.IsDirectory)
             {
                 dirsToVisit.Push((e.FullPath, e.Name, parentDirId));
                 continue;
             }

             var fullPath = PathUtils.NormalizePath(e.FullPath);

             // Normal path: record as enumerated, hash not computed yet.
             FileRecord file = new FileRecord()
             {
                 Created = e.CreationTimeUtc,
                 DirId = parentDirId,
                 Modified = e.ModifiedTimeUtc,
                 Name = e.Name,
                 Size = e.Length,
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
     }
}