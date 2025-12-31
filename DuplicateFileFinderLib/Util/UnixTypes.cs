// Linux-only type checks without allocations beyond path string

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// ReSharper disable InconsistentNaming

namespace DuplicateFileFinderLib.Util;

internal static partial class UnixTypes
{
    private const string LibC = "libc";

    // Big enough that native lstat cannot write past it (avoids clobbering managed stack args).
    // 256 should be plenty for current Linux libcs;
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct StatBuf
    {
        public fixed byte Data[256];
    }

    // .NET 8+ source-generated P/Invoke, correct UTF-8 string marshalling on Linux.
    [LibraryImport(LibC, EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat_legacy(string path, out StatBuf buf);

    // S_IFMT and kinds
    private const uint S_IFMT = 0xF000;
    private const uint S_IFSOCK = 0xC000;
    private const uint S_IFLNK = 0xA000;
    private const uint S_IFREG = 0x8000;
    private const uint S_IFBLK = 0x6000;
    private const uint S_IFDIR = 0x4000;
    private const uint S_IFCHR = 0x2000;
    private const uint S_IFIFO = 0x1000;

    public enum UnixKind
    {
        Unknown,
        Regular,
        Directory,
        Symlink,
        Fifo,
        Socket,
        CharDev,
        BlockDev
    }

    public static bool TryGetKind(string path, out UnixKind kind)
    {
        kind = UnixKind.Unknown;

        if (!OperatingSystem.IsLinux())
            return false;
        if (string.IsNullOrEmpty(path))
            return false;
        if (path.IndexOf('\0') >= 0)
            return false;

        if (lstat_legacy(path, out var st) != 0)
            return false;

        // Linux x86_64: st_mode is at offset 24
        uint mode;
        unsafe
        {
            byte* p = (byte*)Unsafe.AsPointer(ref st);
            mode = Unsafe.ReadUnaligned<uint>(p + 24);
        }

        switch (mode & S_IFMT)
        {
            case S_IFREG:
                kind = UnixKind.Regular;
                return true;
            case S_IFDIR:
                kind = UnixKind.Directory;
                return true;
            case S_IFLNK:
                kind = UnixKind.Symlink;
                return true;
            case S_IFIFO:
                kind = UnixKind.Fifo;
                return true;
            case S_IFSOCK:
                kind = UnixKind.Socket;
                return true;
            case S_IFCHR:
                kind = UnixKind.CharDev;
                return true;
            case S_IFBLK:
                kind = UnixKind.BlockDev;
                return true;
            default:
                return false;
        }

    }
}
