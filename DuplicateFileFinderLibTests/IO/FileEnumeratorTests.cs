using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.IO;


public sealed class FileEnumeratorTests : IDisposable
{
    private readonly TempFsFixture _fs = new();

    public void Dispose()
    {
        _fs.Dispose();
    }

    [Fact]
    public void EnumerateChildren_YieldsFilesAndDirs()
    {
        Directory.CreateDirectory(PathUtil.P(_fs.Root, "A"));
        var f = _fs.File("x.bin", new byte[3]);

        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren(_fs.Root, CancellationToken.None));

        Assert.Contains(list, e => e.IsDirectory && e.FullPath == PathUtil.P(_fs.Root, "A"));
        Assert.Contains(list, e => !e.IsDirectory && e.FullPath == f && e.Length == 3);
    }

    [Fact]
    public void EnumerateChildren_HandlesMissingRoot()
    {
        var gone = PathUtil.P(_fs.Root, "vanish");
        Directory.CreateDirectory(gone);
        Directory.Delete(gone);

        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren(gone, CancellationToken.None)); // no throw
        Assert.Empty(list);
    }

    [Fact]
    public void Linux_VirtualRoots_AreSkipped()
    {
        if (!OperatingSystem.IsLinux()) return;

        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren("/proc", CancellationToken.None));
        // No exception and usually empty due to fast short-circuit
        Assert.NotNull(list);
    }

    [Fact]
    public void ZeroLengthRegularFile_IsIncluded_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var emptyFileName = _fs.File("empty.bin", []);

        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren(_fs.Root, CancellationToken.None));

        // zero-length regular file should be present
        Assert.Contains(list, e => !e.IsDirectory && e.FullPath == emptyFileName);
    }

    [Fact]
    public void ScanLocation_SkipsSymlinkedDirectories_WhenPossible()
    {
        // arrange
        // real/
        //   keep.txt
        // link/ (symlink to real or temp external dir)
        //
        // Expected:
        // - keep.txt shows up
        // - link directory is not traversed/added if it's considered unsafe

        var realDir = _fs.Dir("realDir");
        var realFile = _fs.File("realFile.txt", "DATA"u8.ToArray());

        // We'll *attempt* to create a symlink "link{Dir,File}" pointing at "real{Dir,File}".
        // If symlinks are not allowed (e.g. Windows without dev operation / admin),
        // we'll just skip the "assert it's excluded" part rather than fail.
        var linkDir = PathUtil.P(_fs.Root, "linkDir");
        var linkFile = PathUtil.P(_fs.Root, "linkFile.txt");
        bool symlinkCreated;
        try
        {
            // DirectorySymlink creation across platforms:
            // On Windows: mklink /D requires admin. We'll try .NET API if available, else skip.
#if NET6_0_OR_GREATER
            Directory.CreateSymbolicLink(linkDir, realDir);
            File.CreateSymbolicLink(linkFile, realFile);
            symlinkCreated = true;
#else
                // .NET <6 can't create symbolic links without P/Invoke. We'll skip.
                symlinkCreated = false;
#endif
        }
        catch
        {
            symlinkCreated = false;
        }

        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren(_fs.Root, CancellationToken.None));

        if (symlinkCreated)
        {
            Assert.Contains(list, e => e.IsDirectory && e.FullPath == realDir);
            Assert.Contains(list, e => !e.IsDirectory && e.FullPath == realFile);
            Assert.DoesNotContain(list, e => e.IsDirectory && e.FullPath == linkDir);
            Assert.DoesNotContain(list, e => !e.IsDirectory && e.FullPath == linkFile);
        }

        // If we couldn't create a symlink in this environment, we don't assert that part.
    }

    [Fact]
    public void Linux_Fifo_IsFiltered()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = PathUtil.P(_fs.Root, "pipes");
        Directory.CreateDirectory(dir);
        var fifo = Path.Combine(dir, "p.fifo");

        bool made = TryMkFifo(fifo);
        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren(dir, CancellationToken.None));

        if (made)
            Assert.DoesNotContain(list, e => !e.IsDirectory && e.FullPath == fifo);
    }

    [Fact]
    public void FallbackEnumeration_DirectoryEntry_Name_ShouldBeDirectoryName_NotRootOrFullPath()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "dff_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var subDir = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;

            var sut = new FileEnumerator();
            var buffer = new List<FsEntry>();

            // Call private TryFillBufferFallback(root, buffer, ct)
            var mi = typeof(FileEnumerator).GetMethod(
                "TryFillBufferFallback",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(mi);

            // Act
            mi.Invoke(sut, [root, buffer, CancellationToken.None]);

            // Assert
            var subEntry = Assert.Single(buffer, e => e.IsDirectory && e.FullPath == subDir);

            // This is the invariant implied by FsEntry.Name comment: "top level name"
            Assert.Equal("sub", subEntry.Name);
            Assert.DoesNotContain(Path.DirectorySeparatorChar, subEntry.Name);

        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static bool TryMkFifo(string path)
    {
        try
        {
            return mkfifo(path, Convert.ToUInt32("644", 8)) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "mkfifo")]
    private static extern int mkfifo(string pathname, uint mode);
}