using System.IO;

namespace DuplicateFileFinderLibTests.TestUtils;

public static class PathUtil
{
    // Helper to build nice absolute-ish paths cross-platform
    internal static string AbsPath(params string[] parts) => Path.GetFullPath(Path.Combine(parts));
}