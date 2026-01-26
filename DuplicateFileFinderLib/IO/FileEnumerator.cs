// DuplicateFileFinderLib/FileSystem/FileEnumerator.cs

using System.IO.Enumeration;

using DuplicateFileFinderLib.Util;

using NLog;

namespace DuplicateFileFinderLib.IO;

public readonly record struct FsEntry(
    bool IsDirectory,
    string FullPath,
    string Name,        // top level name
    long Length,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset ModifiedTimeUtc);

public interface IFileEnumerator
{
    IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token);
}

public sealed class FileEnumerator : IFileEnumerator
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private static readonly EnumerationOptions s_enumOpts = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip =
            FileAttributes.ReparsePoint |
            FileAttributes.Device |
            FileAttributes.Offline |
            FileAttributes.IntegrityStream |
            FileAttributes.NoScrubData
    };

    public IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token)
    {
        if (IsVirtualOrEphemeralRoot(dir))
        {
            s_log.Info("Skipping ephemeral directory: {dir}", dir);
            yield break;
        }

        var buffer = new List<FsEntry>(256);

        // Try fast path; if it fails mid-iteration, fall back to safe strategy
        if (!TryFillBufferFast(dir, buffer, token))
            TryFillBufferFallback(dir, buffer, token);

        // Yield from our internal buffer (safe; nothing throws here)
        foreach (var t in buffer)
        {
            token.ThrowIfCancellationRequested();
            yield return t;
        }
    }

    // ---------- FAST PATH (FileSystemEnumerable) ----------

    private static bool TryFillBufferFast(string dir, List<FsEntry> buffer, CancellationToken token)
    {
        FileSystemEnumerable<FsEntry> e;
        try
        {
            e = new FileSystemEnumerable<FsEntry>(
                dir,
                (ref fe) => new FsEntry(
                    fe.IsDirectory,
                    fe.ToFullPath(),
                    fe.FileName.ToString(),
                    fe.Length,
                    fe.CreationTimeUtc,
                    fe.LastWriteTimeUtc),
                s_enumOpts)
            {
                ShouldIncludePredicate = (ref fe) =>
                {
                    if (OperatingSystem.IsLinux())
                    {
                        var full = fe.ToFullPath();
                        if (string.IsNullOrEmpty(full) || IsVirtualOrEphemeralRoot(full))
                            return false;
                    }

                    if (fe.IsDirectory)
                        return true;

                    if (fe.Length > 0)
                        return true;

                    // length == 0 (or odd values)
                    if (OperatingSystem.IsLinux())
                    {
                        var full = fe.ToFullPath();
                        return UnixTypes.TryGetKind(full, out var k) && k == UnixTypes.UnixKind.Regular;
                    }

                    // Windows: treat 0-byte as valid
                    return fe.Length == 0;
                }

            };
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            s_log.Warn(ex, "Aborting enumeration of {path}", dir);
            return true;
        }

        // Consume manually so we can catch MoveNext() errors and bail out cleanly
        using var en = e.GetEnumerator();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            FsEntry current;
            try
            {
                if (!en.MoveNext())
                    break;
                current = en.Current;
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                s_log.Warn(ex, "Aborting fast enumeration of {path}", dir);
                return false;
            }

            buffer.Add(current);
        }

        return true;
    }

    // ---------- FALLBACK (robust but slower) ----------

    private void TryFillBufferFallback(string dir, List<FsEntry> buffer, CancellationToken token)
    {
        buffer.Clear();

        s_log.Info("Attempting fallback directory enumeration of {path}", dir);

        // Step 1: directories
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex)
        {
            s_log.Warn(ex, "Unable to retrieve directory listing. Aborting enumeration of {path}", dir);
            return;
        }

        foreach (var d in dirs)
        {
            token.ThrowIfCancellationRequested();
            DirectoryInfo di = new DirectoryInfo(d);
            var topLevelDirectoryName = Path.GetFileName(d);
            buffer.Add(new FsEntry(
                true,
                d,
                topLevelDirectoryName,
                0,
                di.CreationTimeUtc,
                di.LastWriteTimeUtc));
        }

        // Step 2: files
        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex)
        {
            s_log.Warn(ex, "Unable to retrieve file listing. Skipping file enumeration of {path}", dir);
            return;
        }

        foreach (var f in files)
        {
            token.ThrowIfCancellationRequested();

            string filename;
            long len;
            DateTimeOffset creationTimeUtc;
            DateTimeOffset modifiedTimeUtc;
            try
            {
                var fi = new FileInfo(f);
                len = fi.Length;
                filename = fi.Name;
                creationTimeUtc = fi.CreationTimeUtc;
                modifiedTimeUtc = fi.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                s_log.Warn(ex, "Skipping file {path}", f);

                // Some NTFS special files throw or report -1; skip them
                continue;
            }

            // Include normal files (0-byte or greater)
            if (len >= 0)
                buffer.Add(new FsEntry(false, f, filename, len, creationTimeUtc, modifiedTimeUtc));
        }
    }

    // ---------- Linux virtual/ephemeral roots ----------

    private static bool IsVirtualOrEphemeralRoot(string path)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        var p = path.AsSpan();
        if (p.StartsWith("/proc".AsSpan(), StringComparison.Ordinal))
            return true;
        if (p.StartsWith("/sys".AsSpan(), StringComparison.Ordinal))
            return true;
        if (p.StartsWith("/dev".AsSpan(), StringComparison.Ordinal))
            return true;
        if (p.StartsWith("/run/user/".AsSpan(), StringComparison.Ordinal) &&
            p.Contains("/gvfs".AsSpan(), StringComparison.Ordinal))
            return true;
        return false;
    }
}
