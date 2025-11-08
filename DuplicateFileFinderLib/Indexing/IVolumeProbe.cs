namespace DuplicateFileFinderLib.Indexing;

public interface IVolumeProbe
{
    VolumeId? TryIdentify(string anyPathUnderVolume);
    bool IsLikelySlow(VolumeId id); // rotational | fuseblk | low throughput probe
}