using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

internal static class HashPolicy
{
    public static bool ShouldHash(in FileScanInput f, BaselineIndex baseline, HashPolicyMode mode)
    {
        if (f.Size <= 0)
            return false;

        if (mode == HashPolicyMode.ForceRehash)
            return true;

        // New file => hash
        if (f.FileId <= 0)
            return true;

        if (!baseline.TryGetBaselineFile(f.FileId, out var old))
            return true;

        if (old.Hash == HashKey.NotComputed)
            return true;

        return !(old.Size == f.Size &&
                 old.ModifiedTicks == f.ModifiedTicks);
    }
}
