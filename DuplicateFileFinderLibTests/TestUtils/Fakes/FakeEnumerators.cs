using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DuplicateFileFinderLib.IO;

namespace DuplicateFileFinderLibTests.TestUtils.Fakes;

public sealed class TestEnumerateCanceler(int yieldBeforeSignal, int totalToYield, ManualResetEventSlim signal)
    : IFileEnumerator
{
    public IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token)
    {
        for (int i = 0; i < totalToYield; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i == yieldBeforeSignal) signal.Set();
            var name = $"f{i}.bin";
            yield return new FsEntry(IsDirectory: false,
                FullPath: Path.Combine(dir, name ),
                Length: 1,
                Name: name,
                CreationTimeUtc: DateTimeOffset.Now,
                ModifiedTimeUtc: DateTimeOffset.Now);
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
            var name = $"f{i}.bin";
            yield return new FsEntry(
                false, 
                FullPath: Path.Combine(dir, name),
                name,
                1,
                DateTimeOffset.Now,
                DateTimeOffset.Now);
        }
    }
}