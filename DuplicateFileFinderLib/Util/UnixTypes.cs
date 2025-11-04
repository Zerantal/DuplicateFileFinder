// Linux-only type checks without allocations beyond path string

using System.Runtime.InteropServices;

// ReSharper disable All

namespace DuplicateFileFinderLib.Util;

internal static class UnixTypes
{
#if NET8_0_OR_GREATER
    private const string LibC = "libc";
#else
    private const string LibC = "libc.so.6";
#endif

    // Matches glibc's struct stat on x86_64. We only read st_mode.
    [StructLayout(LayoutKind.Sequential)]
    private struct Stat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode; // <-- file type + perms        
        public uint st_uid;
        public uint st_gid;
        private uint __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime;
        public ulong st_atime_nsec;
        public long st_mtime;
        public ulong st_mtime_nsec;
        public long st_ctime;
        public ulong st_ctime_nsec;
        public long __unused4;
        public long __unused5;
    }

    [DllImport(LibC, SetLastError = true, EntryPoint = "lstat", CharSet = CharSet.Ansi)]
    private static extern int lstat_legacy(string path, out Stat buf);

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

        // Fast exits
        if (!OperatingSystem.IsLinux()) return false;
        if (string.IsNullOrEmpty(path)) return false;
        // Reject embedded NUL which would truncate in libc
        if (path!.IndexOf('\0') >= 0) return false;

        try
        {
            if (lstat_legacy(path, out var st) != 0)
                return false;

            switch (st.st_mode & S_IFMT)
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
                default: return false;
            }
        }
        catch
        {
            return false;
        }
    }
}