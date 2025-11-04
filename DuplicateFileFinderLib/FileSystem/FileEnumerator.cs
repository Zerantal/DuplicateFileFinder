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

                    if (fe.IsDirectory) return true;

                    if (fe.Length > 0) return true; // fast path for regular files

                    if (OperatingSystem.IsLinux())
                    {
                        var full = fe.ToFullPath();
                        return UnixTypes.TryGetKind(full, out var k) && k == UnixTypes.UnixKind.Regular;
                    }

                    return false;
                }
            };
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var item in e)
        {
            token.ThrowIfCancellationRequested();
            yield return item;
        }
    }

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