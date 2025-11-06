// DuplicateFileFinderLib/FileSystem/FileEnumerator.cs

using System.IO.Enumeration;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.FileSystem;

public readonly record struct FsEntry(bool IsDirectory, string FullPath, long Length);

public interface IFileEnumerator
{
    IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token);
}

public sealed class FileEnumerator : IFileEnumerator
{
    private static readonly EnumerationOptions EnumOpts = new()
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
            yield break;

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
                (ref FileSystemEntry fe) => new FsEntry(fe.IsDirectory, fe.ToFullPath(), fe.Length),
                EnumOpts)
            {
                ShouldIncludePredicate = (ref FileSystemEntry fe) =>
                {
                    if (OperatingSystem.IsLinux())
                    {
                        var full = fe.ToFullPath();
                        if (string.IsNullOrEmpty(full) || IsVirtualOrEphemeralRoot(full))
                            return false;
                    }

                    if (fe.IsDirectory) return true; // dirs always included (we decide traversal elsewhere)

                    if (fe.Length > 0) return true;   // fast-path for regular files

                    if (OperatingSystem.IsLinux())
                    {
                        var full = fe.ToFullPath();
                        return UnixTypes.TryGetKind(full, out var k) && k == UnixTypes.UnixKind.Regular;
                    }

                    return false;
                }
            };
        }
        catch (DirectoryNotFoundException) { return true; } // treat as empty
        catch (UnauthorizedAccessException) { return true; } // treat as empty
        catch (IOException) { return false; } // constructor failing → trigger fallback

        // Consume manually so we can catch MoveNext() errors and bail out cleanly
        using var en = e.GetEnumerator();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            FsEntry current;
            try
            {
                if (!en.MoveNext()) break;
                current = en.Current;
            }
            catch (DirectoryNotFoundException) { break; } // this folder vanished → treat as done
            catch (UnauthorizedAccessException) { break; }
            catch (IOException)
            {
                // mid-iteration I/O error → signal caller to use fallback
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
        
        // Step 1: directories
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(dir);
        }
        catch
        {
            return; // skip this folder entirely if even that fails
        }

        foreach (var d in dirs)
        {
            token.ThrowIfCancellationRequested();
            buffer.Add(new FsEntry(true, d, 0));
        }

        // Step 2: files
        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch
        {
            return; // same deal — if the OS refuses, just skip this folder
        }

        foreach (var f in files)
        {
            token.ThrowIfCancellationRequested();

            long len = 0;
            try
            {
                len = new FileInfo(f).Length;
            }
            catch
            {
                // Some NTFS special files throw or report -1; skip them
                continue;
            }

            // Include normal files (0-byte or greater)
            if (len >= 0)
                buffer.Add(new FsEntry(false, f, len));
        }
    }

    // ---------- Linux virtual/ephemeral roots ----------

    private static bool IsVirtualOrEphemeralRoot(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var p = path.AsSpan();
        if (p.StartsWith("/proc".AsSpan(), StringComparison.Ordinal)) return true;
        if (p.StartsWith("/sys".AsSpan(), StringComparison.Ordinal)) return true;
        if (p.StartsWith("/dev".AsSpan(), StringComparison.Ordinal)) return true;
        if (p.StartsWith("/run/user/".AsSpan(), StringComparison.Ordinal) &&
            p.Contains("/gvfs".AsSpan(), StringComparison.Ordinal)) return true;
        return false;
    }
}
