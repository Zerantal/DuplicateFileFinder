namespace DuplicateFileFinderLib.Indexing;

public class LinuxVolumeProbe : IVolumeProbe
{
    public VolumeId? TryIdentify(string anyPathUnderVolume)
    {
        throw new NotImplementedException();
    }

    public bool IsLikelySlow(VolumeId id)
    {
        throw new NotImplementedException();
    }
}