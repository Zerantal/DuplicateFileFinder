using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinderLib.Core;

internal class DuplicateFileFinderHelpers
{
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
        IEnumerable<long> dirsToRemove)
    {
        foreach (var dirId in dirsToRemove)
        {
            var subDirs = treeIndex.GetChildDirIds(dirId);
            var files = treeIndex.GetChildFileIds(dirId);
             
            PurgeOldDirs(session, treeIndex, subDirs);
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