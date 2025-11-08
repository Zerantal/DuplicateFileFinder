namespace DuplicateFileFinderLib.Indexing;

public readonly record struct VolumeId(string FsUuid, string MountPoint, string FsType);