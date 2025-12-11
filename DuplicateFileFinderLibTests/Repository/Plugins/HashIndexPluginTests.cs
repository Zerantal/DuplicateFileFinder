using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins;

public sealed class HashIndexPluginTests
{
    private readonly TempFsFixture _fs = new("hash-index");

    private static HashKey NewHash(int seed)
    {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return new HashKey(bytes);
    }

    private static IRepoView MakeSnapshot(params FileRecord[] files)
    {
        // Dirs/HashIndex are unused by HashIndexPlugin today; provide minimal stubs.
        var fileDict = files.ToDictionary(f => f.FileId, f => f);

        return new RepoView(new Dictionary<long, DirRecord>(), fileDict);
    }

    private static RepoEvent MakeBootstrapEvent(IRepoView snapshot)
    {
        return new BootstrapEvent
        {
            Generation = 1,
            NextLogSequence = 1,
            Snapshot = snapshot
        };
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not satisfied in time.");

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task OpenedEvent_BuildsInitialDuplicateGroups()
    {
        var hashDup = NewHash(1);
        var hashUnique = NewHash(2);

        var f1 = new FileRecord
        {
            FileId = 1,
            DirId = 10,
            Name = "a.bin",
            Size = 100,
            Hash = hashDup,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var f2 = new FileRecord
        {
            FileId = 2,
            DirId = 11,
            Name = "b.bin",
            Size = 100,
            Hash = hashDup,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var f3 = new FileRecord
        {
            FileId = 3,
            DirId = 12,
            Name = "c.bin",
            Size = 100,
            Hash = hashUnique,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var snapshot = MakeSnapshot(f1, f2, f3);

        await using var plugin = new HashIndexPlugin(_fs.Root);

        // Act: simulate repo open
        plugin.Post(MakeBootstrapEvent(snapshot));

        // Wait until the plugin has processed the event and built the index
        await WaitForConditionAsync(
            () => plugin.GetDuplicateGroups().Count > 0,
            TimeSpan.FromSeconds(2));

        var groups = plugin.GetDuplicateGroups();

        // We expect exactly one group (hashDup) with f1 + f2
        var group = Assert.Single(groups);
        Assert.Equal(2, group.list.Count);
        Assert.Contains(1L, group.list);
        Assert.Contains(2L, group.list);
    }

    [Fact]
    public async Task DefaultHash_IsIgnored_AndDoesNotProduceGroup()
    {
        var defaultHash = default(HashKey);

        var f1 = new FileRecord
        {
            FileId = 1,
            DirId = 10,
            Name = "a.bin",
            Size = 100,
            Hash = defaultHash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var f2 = new FileRecord
        {
            FileId = 2,
            DirId = 11,
            Name = "b.bin",
            Size = 100,
            Hash = defaultHash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var snapshot = MakeSnapshot(f1, f2);

        await using var plugin = new HashIndexPlugin(_fs.Root);

        plugin.Post(MakeBootstrapEvent(snapshot));

        // Even though there are two files with the same default hash,
        // the plugin should ignore default hashes and not produce a group.
        await WaitForConditionAsync(
            () => plugin.GetDuplicateGroups().Count == 0,
            TimeSpan.FromSeconds(2));

        Assert.Empty(plugin.GetDuplicateGroups());
    }
}