using System.Text;

namespace DuplicateFileFinderLib.Util;

public static class PathUtils
{
    public static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static StringComparer PathComparer => StringComparer.FromComparison(PathComparison);

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

        bool isWindows = OperatingSystem.IsWindows();
        ReadOnlySpan<char> span = path.AsSpan();

        // Extract Windows drive prefix "C:" (if present).
        string rootPrefix = "";
        int i = 0;
        if (isWindows &&
            span.Length >= 2 &&
            char.IsLetter(span[0]) &&
            span[1] == ':')
        {
            rootPrefix = path[..2]; // "C:"
            i = 2;
        }

        // Determine if the original path starts with a separator.
        bool startedWithSlash =
            span.Length > 0 &&
            (span[0] == '/' || (isWindows && span[0] == '\\'));

        static bool IsSep(char c, bool win) => c == '/' || (win && c == '\\');

        // Store segment start/length pairs referencing the original string.
        var segStarts = new int[Math.Min(64, span.Length / 2 + 1)];
        var segLens = new int[segStarts.Length];
        int segCount = 0;

        void EnsureCapacity()
        {
            if (segCount < segStarts.Length) return;
            Array.Resize(ref segStarts, segStarts.Length * 2);
            Array.Resize(ref segLens, segLens.Length * 2);
        }

        void PushSeg(int start, int len)
        {
            if (len <= 0) return;
            EnsureCapacity();
            segStarts[segCount] = start;
            segLens[segCount] = len;
            segCount++;
        }

        void PopSeg()
        {
            if (segCount > 0) segCount--;
        }

        // Parse segments
        int n = span.Length;
        while (i < n)
        {
            while (i < n && IsSep(span[i], isWindows))
                i++;

            if (i >= n) break;

            int start = i;
            while (i < n && !IsSep(span[i], isWindows))
                i++;

            int len = i - start;

            // "." => skip
            if (len == 1 && span[start] == '.')
                continue;

            // ".." => pop
            if (len == 2 && span[start] == '.' && span[start + 1] == '.')
            {
                if (segCount > 0)
                    PopSeg();
                continue;
            }

            PushSeg(start, len);
        }

        // Compute output length
        int outLen = 0;

        if (!string.IsNullOrEmpty(rootPrefix))
        {
            outLen += 2; // "C:"
            outLen += 1; // "/"
        }
        else if (startedWithSlash)
        {
            outLen += 1; // leading "/"
        }

        if (segCount > 0)
        {
            for (int s = 0; s < segCount; s++)
                outLen += segLens[s];
            outLen += (segCount - 1); // separators between segments
        }

        bool needTrailingSlash = forceTrailingSlash && outLen > 0;

        // If it already ends with '/' structurally (e.g. "/" or "C:/"), don't add another.
        if (needTrailingSlash)
        {
            bool alreadySlashTerminated =
                (segCount == 0) && (startedWithSlash || !string.IsNullOrEmpty(rootPrefix));
            if (!alreadySlashTerminated)
                outLen += 1;
        }

        // Build output
        var state = new NormalizeState(
            path,
            rootPrefix,
            startedWithSlash,
            segStarts,
            segLens,
            segCount,
            needTrailingSlash,
            isWindows);

        return string.Create(outLen, state, static (dst, st) =>
        {
            var src = st.Path.AsSpan();
            int pos = 0;

            if (st.RootPrefix.Length != 0)
            {
                dst[pos++] = st.RootPrefix[0];
                dst[pos++] = st.RootPrefix[1];
                dst[pos++] = '/';
            }
            else if (st.StartedWithSlash)
            {
                dst[pos++] = '/';
            }

            for (int s = 0; s < st.SegCount; s++)
            {
                if (s > 0)
                    dst[pos++] = '/';

                int start = st.SegStarts[s];
                int len = st.SegLens[s];

                src.Slice(start, len).CopyTo(dst.Slice(pos, len));
                pos += len;
            }

            if (st.NeedTrailingSlash)
            {
                if (pos == 0 || dst[pos - 1] != '/')
                    dst[pos++] = '/';
            }
        });
    }

    private readonly struct NormalizeState
    {
        public NormalizeState(
            string path,
            string rootPrefix,
            bool startedWithSlash,
            int[] segStarts,
            int[] segLens,
            int segCount,
            bool needTrailingSlash,
            bool isWindows)
        {
            Path = path;
            RootPrefix = rootPrefix;
            StartedWithSlash = startedWithSlash;
            SegStarts = segStarts;
            SegLens = segLens;
            SegCount = segCount;
            NeedTrailingSlash = needTrailingSlash;
            IsWindows = isWindows;
        }

        public string Path { get; }
        public string RootPrefix { get; }
        public bool StartedWithSlash { get; }
        public int[] SegStarts { get; }
        public int[] SegLens { get; }
        public int SegCount { get; }
        public bool NeedTrailingSlash { get; }
        public bool IsWindows { get; } // kept for possible future tweaks; not used in the builder
    }

    public static string JoinNormalized(string normalizedParentDir, string childName)
    {
        if (string.IsNullOrEmpty(normalizedParentDir))
            return childName;

        // Avoid double slashes. Note: root like "C:/" already ends with '/'
        return normalizedParentDir[^1] == '/'
            ? string.Concat(normalizedParentDir, childName)
            : string.Concat(normalizedParentDir, "/", childName);
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
        else if (n >= 2 && span[1] == ':') // "C:\"
            i = absolutePath.IndexOf(Path.DirectorySeparatorChar) + 1;

        var sb = new StringBuilder();
        for (; i < n; i++)
        {
            var c = span[i];
            if (c == '/' || c == '\\')
            {
                if (sb.Length > 0)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                }
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
    {
        return string.Equals(
            NormalizePath(a),
            NormalizePath(b),
            PathComparison);
    }

    public static bool IsSubPathOf(string candidate, string root)
    {
        candidate = NormalizePath(candidate);
        root = NormalizePath(root);

        if (PathsEqual(candidate, root))
            return true;

        if (!candidate.StartsWith(root, PathComparison))
            return false;

        // Ensure we’re not just matching a prefix (e.g. /home/z2 vs /home/z)
        var ch = candidate.Length > root.Length ? candidate[root.Length] : '\0';
        return ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar;
    }
}