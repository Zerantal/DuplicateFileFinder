// DuplicateFileFinderLib/Repository/Core/Repo.CopyOnWrite.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    private static ScanRootSnapshotView ToView(in ScanRootSnapshotV2 snap)
        => new()
        {
            ScanRootId = snap.ScanRootId,
            StringPool = snap.StringPool,
            Dirs = snap.Dirs,
            Files = snap.Files
        };

    private void UpsertScanRoot_NoLock(in ScanRoot root)
    {
        var copy = new Dictionary<long, ScanRoot>(_scanRoots)
        {
            [root.RootId] = root
        };
        _scanRoots = copy;
    }

    private bool TryUpdateScanRoot_NoLock(long scanRootId, Func<ScanRoot, ScanRoot> updater, out ScanRoot? updated)
    {
        if (!_scanRoots.TryGetValue(scanRootId, out var current))
        {
            updated = null;
            return false;
        }

        updated = updater(current);
        if (Equals(updated, current))
            return true;

        UpsertScanRoot_NoLock(updated);
        return true;
    }

    private void RemoveScanRootSnapshot_NoLock(long scanRootId)
    {
        if (!_scanRootSnapshots.ContainsKey(scanRootId))
            return;

        var copy = new Dictionary<long, ScanRootSnapshotV2>(_scanRootSnapshots);
        copy.Remove(scanRootId);
        _scanRootSnapshots = copy;
    }

    private void UpsertScanRootSnapshot_NoLock(in ScanRootSnapshotV2 snapshot)
    {
        var copy = new Dictionary<long, ScanRootSnapshotV2>(_scanRootSnapshots)
        {
            [snapshot.ScanRootId] = snapshot
        };
        _scanRootSnapshots = copy;
    }

    private void AddScanRun_NoLock(in ScanRun run)
        => UpsertScanRun_NoLock(run.ScanSequence, run);

    private void UpsertScanRun_NoLock(long sequence, in ScanRun run)
    {
        var copy = new Dictionary<long, ScanRun>(_scanRunIndex)
        {
            [sequence] = run
        };
        _scanRunIndex = copy;
    }

    private bool TryUpdateScanRun_NoLock(
        long sequence,
        Func<ScanRun, ScanRun> updater,
        out ScanRun? updated)
    {
        if (!_scanRunIndex.TryGetValue(sequence, out var current))
        {
            updated = null;
            return false;
        }

        updated = updater(current);
        if (Equals(updated, current))
            return true;

        UpsertScanRun_NoLock(sequence, updated);
        return true;
    }
}
