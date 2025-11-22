namespace DuplicateFileFinderLib.Repository.Models;

[Flags]
public enum ScanEntryStatus : byte
{
    None            = 0,
    Enumerated      = 1 << 0, // discovered during enumeration
    Hashed          = 1 << 1, // content hash computed successfully
    Error           = 1 << 2, // failed during enumeration or hashing
    SkippedByFilter = 1 << 3,
    Deleted         = 1 << 4
}