// DuplicateFileFinderLib/Scan/FileEnumerator.cs
using System.IO.Enumeration;
using DuplicateFileFinderLib.Util;
using NLog;

namespace DuplicateFileFinderLib.Scan;

public sealed class FileEnumerator : IFileEnumerator
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    
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

    // ---------------- NEW ASYNC VERSION ----------------
    public async IAsyncEnumerable<FsEntry> EnumerateChildrenAsync(
        string dir,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        // Offload blocking work onto the thread pool to avoid tying up async callers
        await Task.Yield();

        if (IsVirtualOrEphemeralRoot(dir))
        {
            Log.Info("Skipping ephemeral directory: {dir}", dir);
            yield break;
        }

        var buffer = new List<FsEntry>(256);

        bool ok;
        try
        {
            ok = TryFillBufferFast(dir, buffer, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Error during fast enumeration of {path}", dir);
            ok = false;
        }

        if (!ok)
        {
            try
            {
                TryFillBufferFallback(dir, buffer, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Error during fallback enumeration of {path}", dir);
                buffer.Clear();
            }
        }

        foreach (var t in buffer)
        {
            token.ThrowIfCancellationRequested();
            yield return t;

            // cooperative scheduling for large dirs
            if ((buffer.Count & 0xFF) == 0)
                await Task.Yield();
        }
    }

    // Legacy sync version can stay for existing tests until everything switches to async
    public IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token)
    {
        if (IsVirtualOrEphemeralRoot(dir))
        {
            Log.Info("Skipping ephemeral directory: {dir}", dir);
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
                (ref FileSystemEntry fe) => new FsEntry(fe.IsDirectory, fe.ToFullPath(), fe.Length, fe.LastWriteTimeUtc, fe.CreationTimeUtc ),
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

                    if (fe.Length > 0) return true; // fast-path for regular files

                    if (OperatingSystem.IsLinux())
                    {
                        var full = fe.ToFullPath();
                        return UnixTypes.TryGetKind(full, out var k) &&
                               k == UnixTypes.UnixKind.Regular;
                    }

                    return false;
                }
            };
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            Log.Warn(ex, "Aborting enumeration of {path}", dir);
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
                if (!en.MoveNext()) break;
                current = en.Current;
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                Log.Warn(ex, "Aborting fast enumeration of {path}", dir);
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
        
        Log.Info("Attempting fallback directory enumeration of {path}", dir);
        
        // Step 1: directories
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Unable to retrieve directory listing. Aborting enumeration of {path}", dir);
            return; 
        }

        foreach (var d in dirs)
        {
            token.ThrowIfCancellationRequested();
            buffer.Add(new FsEntry(true, d, 0,
                Directory.GetLastWriteTimeUtc(d),
                Directory.GetCreationTimeUtc(d)));
        }

        // Step 2: files
        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Unable to retrieve file listing. Skipping file enumeration of {path}", dir);
            return;
        }

        foreach (var f in files)
        {
            token.ThrowIfCancellationRequested();

            long len;
            DateTimeOffset creationTimeUtc;
            DateTimeOffset lastWriteTimeUtc;
            try
            {
                var fi = new FileInfo(f);
                len = fi.Length;
                creationTimeUtc = fi.CreationTimeUtc;
                lastWriteTimeUtc = fi.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Skipping file {path}", f);
                
                // Some NTFS special files throw or report -1; skip them
                continue;
            }

            // Include normal files (0-byte or greater)
            if (len >= 0)
                buffer.Add(new FsEntry(false, f, len, lastWriteTimeUtc, creationTimeUtc));
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
