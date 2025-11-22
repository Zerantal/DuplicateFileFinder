// Repo/Models/ScanEntryStatus.cs

namespace DuplicateFileFinderLib.Repository.Models;

/// <summary>
/// Discrete status of a scan entry (file or directory).
/// </summary>
public enum ScanEntryStatus : byte
{
    /// <summary>
    /// No information recorded yet.
    /// Note: Non-enumerated dummy entries may have this value.
    /// e.g., Dir entries that are the parents of scan roots
    /// </summary>
    None = 0,

    /// <summary>
    /// Entry was discovered during enumeration, but no hash was computed yet.
    /// (Typically only meaningful during in-flight operations.)
    /// </summary>
    Enumerated = 1,

    /// <summary>
    /// Content hash was computed successfully.
    /// </summary>
    Hashed = 2,

    /// <summary>
    /// Entry was skipped due to an include/exclude filter.
    /// </summary>
    SkippedByFilter = 3,

    /// <summary>
    /// Entry has been deleted since it was last seen.
    /// </summary>
    Deleted = 4,

    /// <summary>
    /// An error occurred when trying to enumerate or hash this entry.
    /// More detail should be in an accompanying error message field.
    /// </summary>
    Error = 5
}