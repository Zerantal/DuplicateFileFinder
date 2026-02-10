namespace DuplicateFileFinderLib.Repository.Core.Scan;

public readonly record struct ScanCompletionInfo(
    ScanRootId ScanRootId,
    long Generation,
    long ScanSequence);
