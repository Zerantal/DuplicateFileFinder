namespace DuplicateFileFinderLib.Repository.Core.Scan;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public readonly record struct DirCursor(long DirId); // opaque handle for traversal
