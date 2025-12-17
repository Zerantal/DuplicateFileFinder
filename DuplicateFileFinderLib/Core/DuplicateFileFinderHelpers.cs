using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using NLog;

namespace DuplicateFileFinderLib.Core;

internal class DuplicateFileFinderHelpers
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

//    ------------ Progress helper ----------------
    internal static void Report(
        IProgress<DuplicateFileFinderProgressReport>? progress,
        ScanPhase phase,
        string message,
        double percent = 0.0,
        bool indeterminate = false,
        long processed = 0,
        long total = 0,
        bool running = true)
    {
        progress?.Report(new DuplicateFileFinderProgressReport
        {
            Phase = phase,
            StatusMessage = message,
            PercentComplete = percent,
            IsIndeterminate = indeterminate,
            Processed = processed,
            Total = total,
            IsRunning = running
        });
    }
    
    internal static void PurgeOldDirs(
        IScanSession session, ITreeIndexReadModel treeIndex,
        IFileDirReadModel fileDirIndex,
        RepoSnapshotView repoView,
        IEnumerable<long> dirsToRemove)
    {
        foreach (var dirId in dirsToRemove)
        {
            if (!fileDirIndex.TryGetDir(dirId, out var dir))
            {
                Log.Warn($"Directory with {dirId} not found when purging directory.");
                continue;
            }

            var subDirs = treeIndex.GetChildDirs(dir).Select(h => repoView.GetDirRecord(h).DirId);
            var files = treeIndex.GetChildFiles(dir).Select(h => repoView.GetFileRecord(h).FileId);
             
            PurgeOldDirs(session, treeIndex, fileDirIndex, repoView, subDirs);
            PurgeOldFiles(session, files);
        
            var dirRecord = new DirRecord
            {
                DirId = dirId,
                Status = ScanEntryStatus.Deleted
            };
            session.AddOrUpdateDirectory(dirRecord);
        }
    }

    internal static void PurgeOldFiles(IScanSession session, IEnumerable<long> filesToRemove)
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