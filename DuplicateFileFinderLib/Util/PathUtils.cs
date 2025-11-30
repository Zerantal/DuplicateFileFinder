using System.Text;

namespace DuplicateFileFinderLib.Util;

public static class PathUtils
{
    /// <summary>
    ///     Normalizes a folder or file path to a canonical form that is OS-agnostic.
    ///     Handles mixed slashes, redundant segments, and cross-platform drive roots.
    /// </summary>
    /// <param name="path">The input path (absolute or relative).</param>
    /// <param name="forceTrailingSlash">Whether to ensure a trailing slash for folder paths.</param>
    /// <returns>Normalized absolute-like path string with consistent separators.</returns>
    public static string NormalizePath(string path, bool forceTrailingSlash = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException(nameof(path));

        bool isWindows = Path.DirectorySeparatorChar == '\\';

        // 1. Unify separators *only* on Windows
        // On Unix '\' is NOT a directory separator and must be preserved.
        string unified = isWindows
            ? path.Replace('\\', '/')
            : path;

        // 2. Handle Windows drive roots like "C:"
        var rootPrefix = "";
        if (isWindows &&
            unified.Length >= 2 &&
            char.IsLetter(unified[0]) &&
            unified[1] == ':')
        {
            rootPrefix = unified[..2]; // "C:"
            unified = unified[2..];
        }

        // 3. Collapse "." and ".." using *only* '/' as a separator
        var parts = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();

        foreach (var part in parts)
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.Pop();
                continue;
            }

            stack.Push(part);
        }

        var collapsed = string.Join("/", stack.Reverse());

        // 4. ReAttach root notation
        var startedWithSlash = path.StartsWith('/') ||
                               (isWindows && path.StartsWith("\\"));
        string normalized;

        if (!string.IsNullOrEmpty(rootPrefix))
            normalized = rootPrefix + "/" + collapsed;
        else if (startedWithSlash)
            normalized = "/" + collapsed;
        else
            normalized = collapsed;

        // 5. Remove accidental double slashes
        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");

        // 6. Optional trailing slash for directories
        if (forceTrailingSlash &&
            !string.IsNullOrEmpty(normalized) &&
            !normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return normalized;
    }

    public static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static StringComparer PathComparer => StringComparer.FromComparison(PathComparison);
    
    public static string? GetParentPath(string path)
    {
        return Path.GetDirectoryName(NormalizePath(path));
    }

    public static bool IsSamePath(string a, string b)
    {
        return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAncestorOfPath(string ancestor, string path)
    {
        var a = NormalizePath(ancestor).TrimEnd(Path.DirectorySeparatorChar);
        var p = NormalizePath(path);
        if (string.Equals(a, p, StringComparison.OrdinalIgnoreCase)) return false;
        return p.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    
    public static List<string> SplitPath(string absolutePath)
    {
        // Produce components without root prefix artifacts
        var parts = new List<string>();
        var span = absolutePath.AsSpan();
        int i = 0, n = span.Length;

        // Skip leading slash on Unix, or drive root on Windows
        if (n >= 1 && (span[0] == '/' || span[0] == '\\'))
            i = 1;
        else if (n >= 2 && span[1] == ':' ) // "C:\"
            i = absolutePath.IndexOf(Path.DirectorySeparatorChar) + 1;

        var sb = new StringBuilder();
        for (; i < n; i++)
        {
            char c = span[i];
            if (c == '/' || c == '\\')
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    public static bool PathsEqual(string a, string b)
        => string.Equals(
            NormalizePath(a),
            NormalizePath(b),
            PathComparison);
    
    public static bool IsSubPathOf(string candidate, string root)
    {
        candidate = PathUtils.NormalizePath(candidate);
        root = PathUtils.NormalizePath(root);

        if (PathUtils.PathsEqual(candidate, root))
            return true;

        if (!candidate.StartsWith(root, PathComparison))
            return false;

        // Ensure we’re not just matching a prefix (e.g. /home/z2 vs /home/z)
        var ch = candidate.Length > root.Length ? candidate[root.Length] : '\0';
        return ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar;
    }
}