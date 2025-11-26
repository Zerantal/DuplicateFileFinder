// DuplicateFileFinderLib/IO/IVolumeInfoProvider.cs

namespace DuplicateFileFinderLib.IO;

public interface IVolumeInfoProvider
{
    /// <summary>
    /// Returns information about the volume that contains the given path.
    /// Implementations may throw if the path does not exist.
    /// </summary>
    VolumeInfo GetVolumeInfoForPath(string rootPath);
}