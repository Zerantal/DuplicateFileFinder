using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

using DuplicateFileFinderLibTests.TestUtils;

using Xunit;

namespace DuplicateFileFinderLibTests.Core;

public sealed class FullScanOperationE2ETests
{
    private readonly TempFsFixture _tempFsFixture = new("dff_E2E_tests");

    [Fact]
    public async Task FullScan_PersistsSnapshot_AndComputesMd5Hashes()
    {
        var repoDir = _tempFsFixture.Dir("repo");
        var root = _tempFsFixture.Dir("root");

        // Arrange filesystem
        var aPath = Path.Combine(root, "a.txt");
        await File.WriteAllTextAsync(aPath, "hello world\n", TestContext.Current.CancellationToken);

        var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        var bPath = Path.Combine(sub, "b.bin");
        await File.WriteAllBytesAsync(
            bPath,
            Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray(),
            TestContext.Current.CancellationToken);

        // zero-byte file: should exist, but HashPolicy.ShouldHash returns false for size <= 0
        var zPath = Path.Combine(root, "zero.dat");
        await File.WriteAllBytesAsync(zPath, [], TestContext.Current.CancellationToken);

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        // Act
        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        // Assert run completed
        var run = Assert.Single(host.Repo.ScanRunsView);
        Assert.Equal(ScanRunStatus.Completed, run.Status);

        var snap = host.Repo.TryGetScanRootView(run.ScanRootId);
        Assert.NotNull(snap);

        var fileByRelPath = SnapshotHelpers.BuildFileMap(snap);

        // a.txt hash matches MD5
        Assert.True(fileByRelPath.TryGetValue("a.txt", out var aRec));
        Assert.True(aRec.Hash.IsComputed);
        Assert.Equal(Md5HashKey(aPath), aRec.Hash);

        // sub/b.bin hash matches MD5
        Assert.True(fileByRelPath.TryGetValue(PathUtils.NormalizePath(Path.Combine("sub", "b.bin")), out var bRec));
        Assert.True(bRec.Hash.IsComputed);
        Assert.Equal(Md5HashKey(bPath), bRec.Hash);

        // zero-byte file is present, but hash remains NotComputed (because it wasn't queued)
        Assert.True(fileByRelPath.TryGetValue("zero.dat", out var zRec));
        Assert.True(zRec.Hash.IsNotComputed);
    }

    [Fact]
    public async Task DefaultHashPolicy_ReusesBaselineHash_ForUnchangedUnreadableFiles()
    {
        var repoDir = _tempFsFixture.Dir("repo");
        var root = _tempFsFixture.Dir("root");

        var stablePath = Path.Combine(root, "stable.txt");
        await File.WriteAllTextAsync(stablePath, "I should not be re-hashed if unchanged.\n", TestContext.Current.CancellationToken);

        var changedPath = Path.Combine(root, "changed.txt");
        await File.WriteAllTextAsync(changedPath, "v1\n", TestContext.Current.CancellationToken);

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        // First scan (baseline established)
        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run1 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run1.Status);

        var snap1 = host.Repo.TryGetScanRootView(run1.ScanRootId);
        Assert.NotNull(snap1);
        var map1 = SnapshotHelpers.BuildFileMap(snap1);

        var stableHash1 = map1["stable.txt"].Hash;
        Assert.True(stableHash1.IsComputed);

        // Make stable file unreadable but unchanged (chmod changes ctime, not mtime)
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(stablePath, UnixFileMode.None);
        }

        // Mutate only changed.txt content (mtime/size should change)
        await File.WriteAllTextAsync(changedPath, "v2\n", TestContext.Current.CancellationToken);

        // Second scan using Default policy => should hash only changed.txt
        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run2 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run2.Status);

        var snap2 = host.Repo.TryGetScanRootView(run2.ScanRootId);
        Assert.NotNull(snap2);
        var map2 = SnapshotHelpers.BuildFileMap(snap2);

        // stable.txt should still have the same computed hash (reused from baseline)
        Assert.Equal(stableHash1, map2["stable.txt"].Hash);

        // changed.txt should have a new computed hash
        Assert.True(map2["changed.txt"].Hash.IsComputed);
        Assert.Equal(Md5HashKey(changedPath), map2["changed.txt"].Hash);

        // Reset permissions for cleanup on unix
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(stablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task DefaultHashPolicy_DoesNotChangeHashes_WhenNothingChanges()
    {
        var repoDir = _tempFsFixture.Dir("repo");
        var root = _tempFsFixture.Dir("root");

        var p1 = Path.Combine(root, "a.txt");
        var p2 = Path.Combine(root, "b.txt");

        await File.WriteAllTextAsync(p1, "aaa\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(p2, "bbb\n", TestContext.Current.CancellationToken);

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run1 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run1.Status);

        var snap1 = host.Repo.TryGetScanRootView(run1.ScanRootId);
        Assert.NotNull(snap1);
        var map1 = SnapshotHelpers.BuildFileMap(snap1);

        var h1A = map1["a.txt"].Hash;
        var h1B = map1["b.txt"].Hash;
        Assert.True(h1A.IsComputed);
        Assert.True(h1B.IsComputed);

        // Rescan without changes
        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run2 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run2.Status);

        var snap2 = host.Repo.TryGetScanRootView(run2.ScanRootId);
        Assert.NotNull(snap2);
        var map2 = SnapshotHelpers.BuildFileMap(snap2);

        Assert.Equal(h1A, map2["a.txt"].Hash);
        Assert.Equal(h1B, map2["b.txt"].Hash);
    }

    [Fact]
    public async Task Rescan_WithDeletedFile_MarksFileDeletedInSnapshot()
    {
        var repoDir = _tempFsFixture.Dir("repo");
        var root = _tempFsFixture.Dir("root");

        var keep = Path.Combine(root, "keep.txt");
        var del = Path.Combine(root, "delete.txt");

        await File.WriteAllTextAsync(keep, "keep\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(del, "delete\n", TestContext.Current.CancellationToken);

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        File.Delete(del);

        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run2 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run2.Status);

        var snap2 = host.Repo.TryGetScanRootView(run2.ScanRootId);
        Assert.NotNull(snap2);
        var map2 = SnapshotHelpers.BuildFileMap(snap2);

        Assert.True(map2.TryGetValue("keep.txt", out var f1) && f1.Status == ScanEntryStatus.Hashed);
        Assert.True(map2.TryGetValue("delete.txt", out var f2) && f2.Status == ScanEntryStatus.Deleted);

    }


    [Fact]
    public async Task Rescan_WithDeletedDirectory_RemovesDirectoryAndChildrenFromSnapshot()
    {
        var repoDir = _tempFsFixture.Dir("repo");
        var root = _tempFsFixture.Dir("root");

        var keep = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep\n", TestContext.Current.CancellationToken);

        var subDir = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        var child = Path.Combine(subDir, "child.txt");
        await File.WriteAllTextAsync(child, "child\n", TestContext.Current.CancellationToken);

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        Directory.Delete(subDir, recursive: true);

        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run2 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run2.Status);

        var snap2 = host.Repo.TryGetScanRootView(run2.ScanRootId);
        Assert.NotNull(snap2);
        var map2 = SnapshotHelpers.BuildFileMap(snap2);

        Assert.True(map2.ContainsKey("keep.txt"));
        Assert.False(map2.ContainsKey(PathUtils.NormalizePath(Path.Combine("sub", "child.txt"))));
    }

    [Fact]
    public async Task ForceRehash_UnchangedUnreadableFile_FailsOrThrows()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            return;

        var repoDir = _tempFsFixture.Dir("repo");
        var root = _tempFsFixture.Dir("root");

        var stablePath = Path.Combine(root, "stable.txt");
        await File.WriteAllTextAsync(stablePath, "baseline\n", TestContext.Current.CancellationToken);

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        // Baseline scan with Default policy
        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run1 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run1.Status);

        // Make unreadable and keep unchanged.
        File.SetUnixFileMode(stablePath, UnixFileMode.None);

        try
        {
            // Force rehash should attempt to read and therefore either throw or record a failure/error.
            await op.ExecuteAsync(
                root,
                new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.ForceRehash),
                progress: null,
                ct: CancellationToken.None);

            // If no exception, assert we observed *some* indication that the file could not be rehashed.
            // Implementations vary: the scan might fail, or it might complete with per-file errors.
            var run2 = host.Repo.ScanRunsView.Last();
            var snap2 = host.Repo.TryGetScanRootView(run2.ScanRootId);
            Assert.NotNull(snap2);
            var map2 = SnapshotHelpers.BuildFileMap(snap2);

            Assert.True(map2.TryGetValue("stable.txt", out var stableRec));
            var err = stableRec.ErrorMessageStrIdx >= 0 ? snap2.StringPool.GetString(stableRec.ErrorMessageStrIdx) : string.Empty;

            Assert.True(
                run2.Status != ScanRunStatus.Completed ||
                stableRec.Hash.IsNotComputed ||
                !string.IsNullOrWhiteSpace(err));
        }
        catch (UnauthorizedAccessException)
        {
            // acceptable: hashing pipeline attempted to read and the OS denied it
            var run2 = host.Repo.ScanRunsView.Last();
            Assert.NotEqual(ScanRunStatus.Completed, run2.Status);
        }
        finally
        {
            File.SetUnixFileMode(stablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Cancellation_MarksRunCancelled_AndThrowsOperationCanceledException()
    {
        var repoDir = _tempFsFixture.Dir("repo");
        var root = _tempFsFixture.Dir("root");

        // Create a moderately sized tree to ensure we cancel during enumeration/hash.
        for (int d = 0; d < 40; d++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"d{d:00}")).FullName;
            for (int f = 0; f < 200; f++)
            {
                var path = Path.Combine(dir, $"f{f:000}.bin");
                await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(8 * 1024), TestContext.Current.CancellationToken);
            }
        }

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            op.ExecuteAsync(
                root,
                new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
                progress: null,
                ct: cts.Token));

        var run = host.Repo.ScanRunsView.Last();
        // Repo stores Cancelled as ScanRunStatus.Cancelled in MarkScanFailed(cancelled:true)
        Assert.Equal(ScanRunStatus.Cancelled, run.Status);
    }

    private static HashKey Md5HashKey(string fullPath)
    {
        var bytes = MD5.HashData(File.ReadAllBytes(fullPath));
        return new HashKey(bytes);
    }

    private static class SnapshotHelpers
    {
        public static Dictionary<string, FileRecordV2> BuildFileMap(ScanRootSnapshotView snap)
        {
            var dirs = snap.Dirs;
            var pool = snap.StringPool;

            var dirById = dirs.ToDictionary(d => d.DirId);
            var pathCache = new Dictionary<long, string>();

            string DirPath(long dirId)
            {
                if (dirId <= 0)
                    return "";
                if (pathCache.TryGetValue(dirId, out var cached))
                    return cached;

                if (!dirById.TryGetValue(dirId, out var dRec))
                    return pathCache[dirId] = "";

                var name = dRec.NameStrIdx >= 0 ? pool.GetString(dRec.NameStrIdx) : "";

                // Root dir has empty name
                var parent = dRec.ParentDirId;
                var parentPath = parent > 0 ? DirPath(parent) : "";

                var full = string.IsNullOrEmpty(parentPath)
                    ? name
                    : string.IsNullOrEmpty(name) ? parentPath : Path.Combine(parentPath, name);

                pathCache[dirId] = full;
                return full;
            }

            var map = new Dictionary<string, FileRecordV2>(StringComparer.Ordinal);
            foreach (var f in snap.Files)
            {
                var fileName = f.NameStrIdx >= 0 ? pool.GetString(f.NameStrIdx) : "";
                var dirPath = DirPath(f.DirId);

                var rel = string.IsNullOrEmpty(dirPath) ? fileName : Path.Combine(dirPath, fileName);
                map[PathUtils.NormalizePath(rel)] = f;
            }

            // Normalize keys for stable assertions across OS path separators
            return map.ToDictionary(kvp => PathUtils.NormalizePath(kvp.Key), kvp => kvp.Value, StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task FolderRescan_DoesNotMarkDeletionsOutsideSubtree()
    {
        var repoDir = _tempFsFixture.Dir("repo_folder_rescan");
        var root = _tempFsFixture.Dir("root_folder_rescan");

        var subDir = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        var otherDir = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;

        var subFile = Path.Combine(subDir, "keep.txt");
        var otherFile = Path.Combine(otherDir, "delete.txt");
        await File.WriteAllTextAsync(subFile, "v1\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(otherFile, "v1\n", TestContext.Current.CancellationToken);

        await using var host = await RepoHost.OpenAsync(repoDir, TestContext.Current.CancellationToken);

        var fs = new FileEnumerator();
        var hashingRunner = new HashingRunner<FileHashToken>(new ChecksumPipelineMD5());
        var op = new FullScanOperation(host, fs, hashingRunner, volumeInfoProvider: null);

        // Baseline full scan
        await op.ExecuteAsync(
            root,
            new ScanOptions(StartFresh: true, HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run1 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run1.Status);

        var snap1 = host.Repo.TryGetScanRootView(run1.ScanRootId);
        Assert.NotNull(snap1);

        // Get DirHandle for "sub" directory
        var subIdx = snap1.Dirs
            .Select((d, i) => new { d, i })
            .First(x => x.d.ParentDirId >= 0 && snap1.StringPool.GetString(x.d.NameStrIdx) == "sub").i;
        var subHandle = new DuplicateFileFinderLib.Repository.Plugins.Models.DirHandle(run1.ScanRootId, subIdx);

        // Delete a file outside the subtree and mutate inside subtree
        File.Delete(otherFile);
        await File.WriteAllTextAsync(subFile, "v2\n", TestContext.Current.CancellationToken);

        // Subtree rescan only
        await op.ExecuteAsync(
            subHandle,
            new ScanOptions(HashPolicy: HashPolicyMode.Default),
            progress: null,
            ct: CancellationToken.None);

        var run2 = host.Repo.ScanRunsView.Last();
        Assert.Equal(ScanRunStatus.Completed, run2.Status);

        var snap2 = host.Repo.TryGetScanRootView(run2.ScanRootId);
        Assert.NotNull(snap2);

        var map2 = SnapshotHelpers.BuildFileMap(snap2);

        // File deleted outside subtree should NOT be marked deleted because its parent wasn't enumerated.
        var relOther = PathUtils.NormalizePath(Path.Combine("other", "delete.txt"));
        Assert.True(map2.TryGetValue(relOther, out var otherRec));
        Assert.NotEqual(ScanEntryStatus.Deleted, otherRec.Status);

        // File inside subtree should have updated hash (it was rehashed due to change)
        var relSub = PathUtils.NormalizePath(Path.Combine("sub", "keep.txt"));
        Assert.True(map2.TryGetValue(relSub, out var subRec));
        Assert.True(subRec.Hash.IsComputed);
        Assert.Equal(Md5HashKey(subFile), subRec.Hash);
    }

}
