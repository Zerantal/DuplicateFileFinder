using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DuplicateFileFinderLib.Scan;

namespace DuplicateFileFinderLibTests.TestUtils;

public sealed class TestEnumerateCanceler(int yieldBeforeSignal, int totalToYield, ManualResetEventSlim signal)
    : IFileEnumerator
{
    public IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token)
    {
        for (int i = 0; i < totalToYield; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i == yieldBeforeSignal) signal.Set();
            yield return new FsEntry(IsDirectory: false,
                FullPath: Path.Combine(dir, $"f{i}.bin"),
                Length: 1,
                LastWriteTimeUtc: DateTimeOffset.Now,
                CreationTimeUtc: DateTimeOffset.Now);
        }
    }
}

public sealed class TestEnumeratorThrower(int throwOnIndex) : IFileEnumerator
{
    public IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token)
    {
        for (int i = 0; i < 100; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i == throwOnIndex) throw new IOException("Injected iterator failure");
            yield return new FsEntry(
                false,
                Path.Combine(dir, $"f{i}.bin"),
                10,
                DateTimeOffset.Now,
                DateTimeOffset.Now);
        }
    }
}