namespace DuplicateFileFinderLib.Core;

public sealed class ScanLocationNotMountedException(string message) : Exception(message);
