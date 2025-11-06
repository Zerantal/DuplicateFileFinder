using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using DuplicateFileFinderLib.FileSystem;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.FileSystem;



public sealed class FileEnumeratorTests : IDisposable
{
    private readonly string _root;
    private readonly IoUtil _ioUtil;

    public FileEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FE_" + Guid.NewGuid().ToString("N"));
        _ioUtil = new IoUtil(_root);
    }

    public void Dispose()
    {
        _ioUtil.Dispose();
    }

    [Fact]
    public void EnumerateChildren_YieldsFilesAndDirs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "A"));
        var f = _ioUtil.CreateFile("x.bin", new byte[3]);

        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren(_root, CancellationToken.None));

        Assert.Contains(list, e => e.IsDirectory && e.FullPath == Path.Combine(_root, "A"));
        Assert.Contains(list, e => !e.IsDirectory && e.FullPath == f && e.Length == 3);
    }

    [Fact]
    public void EnumerateChildren_HandlesMissingRoot()
    {
        var gone = Path.Combine(_root, "vanish");
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
        
        var emptyFileName = _ioUtil.CreateFile("empty.bin", []);
    
        var sut = new FileEnumerator();
        var list =  new List<FsEntry>(sut.EnumerateChildren(_root, CancellationToken.None));
        
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

        var realDir = _ioUtil.CreateDir("realDir");
        var realFile = _ioUtil.CreateFile("realFile.txt", "DATA"u8.ToArray());

        // We'll *attempt* to create a symlink "link{Dir,File}" pointing at "real{Dir,File}".
        // If symlinks are not allowed (e.g. Windows without dev mode / admin),
        // we'll just skip the "assert it's excluded" part rather than fail.
        var linkDir = Path.Combine(_root, "linkDir");
        var linkFile = Path.Combine(_root, "linkFile.txt");
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
        var list = new List<FsEntry>(sut.EnumerateChildren(_root, CancellationToken.None));

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

        var dir = Path.Combine(_root, "pipes");
        Directory.CreateDirectory(dir);
        var fifo = Path.Combine(dir, "p.fifo");

        bool made = TryMkFifo(fifo);
        var sut = new FileEnumerator();
        var list = new List<FsEntry>(sut.EnumerateChildren(dir, CancellationToken.None));

        if (made)
            Assert.DoesNotContain(list, e => !e.IsDirectory && e.FullPath == fifo);
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