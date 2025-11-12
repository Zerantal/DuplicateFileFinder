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

        // 1. Unify separators
        var unified = path.Replace('\\', '/');

        // 2. Handle Windows drive roots (e.g. "C:/...") specially.
        var rootPrefix = "";
        if (unified.Length >= 2 && char.IsLetter(unified[0]) && unified[1] == ':')
        {
            rootPrefix = unified[..2]; // "C:"
            unified = unified[2..];
        }

        // 3. Split and collapse "." and ".."
        var parts = unified.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
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

        // 4. Reattach root prefix and leading slash if it started with / or a drive
        var startedWithSlash = path.StartsWith('/') || path.StartsWith('\\');
        string normalized;

        if (!string.IsNullOrEmpty(rootPrefix))
            normalized = rootPrefix + "/" + collapsed;
        else if (startedWithSlash)
            normalized = "/" + collapsed;
        else
            normalized = collapsed;

        // 5. Remove duplicate slashes
        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");

        // 6. Optionally ensure trailing slash for folder paths
        if (forceTrailingSlash && !string.IsNullOrEmpty(normalized) && !normalized.EndsWith('/'))
            normalized += "/";

        return normalized;
    }

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
}