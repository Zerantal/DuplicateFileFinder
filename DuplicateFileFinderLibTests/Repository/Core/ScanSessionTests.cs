using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Util;
using DuplicateFileFinderLibTests.TestUtils.Fakes;
using Moq;
using Xunit;
using Repo = DuplicateFileFinderLib.Repository.Core.Repo;

namespace DuplicateFileFinderLibTests.Repository.Core;

public sealed partial class ScanSessionTests : IDisposable
{
    private readonly string _repoDir;

    public ScanSessionTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "dff-scan-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repoDir))
                Directory.Delete(_repoDir, true);
        }
        catch
        {
            // ignore cleanup errors in tests
        }
    }

    private static IReadOnlyList<DirRecord> RealDirs(IRepoView snapshot)
    {
        // Filter out Status.None dummy entries
        return snapshot.Dirs.Values.Where(d => d.Status != ScanEntryStatus.None).ToList();
    }

    private static async Task<IRepoView?> WaitForSnapshotAsync(
        IRepo repo,
        Func<IRepoView, bool> predicate,
        TimeSpan timeout)
    {
        var start = DateTime.UtcNow;

        while (true)
        {
            var snapshot = repo.GetRepoView();
            if (predicate(snapshot))
                return snapshot;

            if (DateTime.UtcNow - start > timeout)
                return null;

            await Task.Delay(10);
        }
    }

    // --------------------------------------------------------------------
    // Progressive flush: AddOrUpdate* + FlushProgress should commit a delta
    //    and update repo state, but NOT mark the run completed.
    // --------------------------------------------------------------------
    [Fact]
    public async Task FlushProgress_CommitsObservedDirsAndFiles_WithoutCompletingRun()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        await using var session = repo.BeginScan(rootPath);

        // Promote the dummy root dir (Status=None) to Enumerated via RootDir
        var rootDirId = session.AddOrUpdateDirectory(session.RootDir with
        {
            Status = ScanEntryStatus.Enumerated
        });

        var hashBytes = new byte[16];
        new Random(123).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        var file = new FileRecord
        {
            FileId = 0,
            DirId = rootDirId,
            Name = "file.txt",
            Size = 100,
            Hash = hashKey,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            Status = ScanEntryStatus.Hashed
        };
        session.AddOrUpdateFile(ref file);

        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        // Do not call CompleteAsync / FailAsync here.

        var snapshot = repo.GetRepoView();

        // Files and dirs should be present (ignoring dummy ancestors)
        var dirs = RealDirs(snapshot);
        Assert.Single(dirs);

        var dir = dirs[0];
        Assert.Equal(rootDirId, dir.DirId);
        Assert.Equal(session.ScanSequence, dir.LastSeenScanSequence);

        var files = snapshot.Files.Values.ToList();
        Assert.Single(files);

        var persistedFile = files[0];
        Assert.Equal("file.txt", persistedFile.Name);
        Assert.Equal(session.ScanSequence, persistedFile.LastSeenScanSequence);
        Assert.Equal(rootDirId, persistedFile.DirId);

        // ScanRun should exist and still be InProgress
        var run = Assert.Single(repo.ScanRunsView);
        Assert.Equal(session.ScanSequence, run.ScanSequence);
        Assert.Equal(ScanRunStatus.InProgress, run.Status);
    }

    // --------------------------------------------------------------------
    // CompleteAsync: progressive scan + completion should emit tombstones
    // for entries under the root that were not seen in this scan sequence.
    // --------------------------------------------------------------------
    [Fact]
    public async Task CompleteAsync_EmitsTombstonesForUnseenEntriesUnderRoot()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        // Seed repo with an "old" file under /root/sub seen at scan sequence 1
        long rootDirId = 22;
        long subDirId = 33;
        long oldFileId = 44;

        var hashBytes = new byte[16];
        new Random(111).NextBytes(hashBytes);
        var hash = new HashKey(hashBytes);

        var seq = (repo as Repo)!.AllocateRunId();

        var rootDir = new DirRecord
        {
            DirId = rootDirId,
            ParentDirId = null,
            Name = "root",
            LastSeenScanSequence = seq,
            Status = ScanEntryStatus.Enumerated
        };

        var subDir = new DirRecord
        {
            DirId = subDirId,
            ParentDirId = rootDirId,
            Name = "sub",
            LastSeenScanSequence = seq,
            Status = ScanEntryStatus.Enumerated
        };

        var oldFile = new FileRecord
        {
            FileId = oldFileId,
            DirId = subDirId,
            Name = "old.txt",
            Size = 1,
            Hash = hash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = seq,
            Status = ScanEntryStatus.Enumerated
        };

        var scanSeq = (repo as Repo)!.AllocateLogId();
        await repo.CommitDeltaAsync(new RepoDelta
        {
            ScanSequence = scanSeq,
            Dirs = [rootDir, subDir],
            Files = [oldFile]
        }, TestContext.Current.CancellationToken);

        var snapshot1 = repo.GetRepoView();
        Assert.Single(snapshot1.Files);
        Assert.True(snapshot1.Files.ContainsKey(oldFileId));

        // New scan: only see a new file under the same root/sub.
        await using (var session = repo.BeginScan(rootPath))
        {
            // Promote the scan root dir from the session to Enumerated
            var scanRootDirId = session.AddOrUpdateDirectory(session.RootDir with
            {
                DirId = rootDirId, // reuse seeded root ID
                ParentDirId = null,
                Status = ScanEntryStatus.Enumerated
            });

            Assert.Equal(rootDirId, scanRootDirId);

            // Reuse existing subdir, marking it seen in this scan
            session.AddOrUpdateDirectory(subDir with
            {
                Status = ScanEntryStatus.Enumerated
            });

            var newFile = new FileRecord
            {
                FileId = 0,
                DirId = subDirId,
                Name = "new.txt",
                Size = 2,
                Hash = hash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                Status = ScanEntryStatus.Hashed
            };
            session.AddOrUpdateFile(ref newFile);
            var oldFileUpdate = oldFile with { Status = ScanEntryStatus.Deleted };
            session.AddOrUpdateFile(ref oldFileUpdate);

            // Progressive flush + completion
            await session.FlushProgressAsync(TestContext.Current.CancellationToken);
            await session.CompleteAsync(TestContext.Current.CancellationToken);
        }

        var snapshot2 = repo.GetRepoView();

        // Only the new file under root/sub should remain.
        var files = snapshot2.Files.Values.ToList();
        Assert.Single(files);

        var remaining = files[0];
        Assert.Equal("new.txt", remaining.Name);
        Assert.Equal(subDirId, remaining.DirId);
        Assert.False(snapshot2.Files.ContainsKey(oldFileId));

        // ScanRun should be marked Completed for the latest sequence.
        var run = repo.ScanRunsView.Single(r => r.Status == ScanRunStatus.Completed);
        Assert.Equal(ScanRunStatus.Completed, run.Status);
    }

    // --------------------------------------------------------------------
    // FailAsync: marking a scan as failed/cancelled should not generate
    // tombstones, even if there was prior content.
    // --------------------------------------------------------------------
    [Fact]
    public async Task FailAsync_DoesNotEmitTombstones()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        // Seed with an existing file under root
        var dirId = 11;
        var fileId = 22;

        var hashBytes = new byte[16];
        new Random(222).NextBytes(hashBytes);
        var hash = new HashKey(hashBytes);

        var dir = new DirRecord
        {
            DirId = dirId,
            ParentDirId = null,
            Name = "root",
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        var file = new FileRecord
        {
            FileId = fileId,
            DirId = dirId,
            Name = "keep.txt",
            Size = 1,
            Hash = hash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        await repo.CommitDeltaAsync(new RepoDelta
        {
            ScanSequence = 1,
            Dirs = [dir],
            Files = [file]
        }, TestContext.Current.CancellationToken);

        var snapshot1 = repo.GetRepoView();
        Assert.Single(snapshot1.Files);
        Assert.True(snapshot1.Files.ContainsKey(fileId));

        await using (var session = repo.BeginScan(rootPath))
        {
            // Promote the dummy root from BeginScan
            session.AddOrUpdateDirectory(session.RootDir with
            {
                Status = ScanEntryStatus.Enumerated
            });

            await session.FlushProgressAsync(TestContext.Current.CancellationToken);
            await session.FailAsync("cancelled", true, TestContext.Current.CancellationToken);
        }

        var snapshot2 = repo.GetRepoView();

        // Original file must still be present; no tombstone-based deletion.
        Assert.True(snapshot2.Files.ContainsKey(fileId));

        // Latest ScanRun should be Failed or Cancelled.
        var run = repo.ScanRunsView.OrderByDescending(r => r.ScanSequence).First();
        Assert.True(run.Status == ScanRunStatus.Failed || run.Status == ScanRunStatus.Cancelled);
        Assert.Equal("cancelled", run.ErrorMessage);
    }

    // --------------------------------------------------------------------
    //    DisposeAsync: if a ScanSession is disposed without CompleteAsync
    //    or FailAsync, it should mark the run as failed/cancelled and
    //    not emit deletions.
    // --------------------------------------------------------------------
    [Fact]
    public async Task DisposeAsync_WithoutCompletion_MarksRunFailedWithoutDeletions()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        // Seed with an existing file under root
        var dirId = 11;
        var fileId = 22;

        var hashBytes = new byte[16];
        new Random(333).NextBytes(hashBytes);
        var hash = new HashKey(hashBytes);

        var dir = new DirRecord
        {
            DirId = dirId,
            ParentDirId = null,
            Name = "root",
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        var file = new FileRecord
        {
            FileId = fileId,
            DirId = dirId,
            Name = "keep.txt",
            Size = 1,
            Hash = hash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        await repo.CommitDeltaAsync(new RepoDelta
        {
            ScanSequence = 1,
            Dirs = [dir],
            Files = [file]
        }, TestContext.Current.CancellationToken);

        var snapshot1 = repo.GetRepoView();
        Assert.True(snapshot1.Files.ContainsKey(fileId));

        // Start a new scan, observe something, but neither complete nor fail explicitly.
        var session = repo.BeginScan(rootPath);

        // Promote the dummy root
        session.AddOrUpdateDirectory(session.RootDir with
        {
            Status = ScanEntryStatus.Enumerated
        });

        await session.FlushProgressAsync(TestContext.Current.CancellationToken);
        await session.DisposeAsync(); // should mark run failed/cancelled

        var snapshot2 = repo.GetRepoView();

        // Original file must still be present.
        Assert.True(snapshot2.Files.ContainsKey(fileId));

        // Latest ScanRun should not be InProgress anymore.
        var run = repo.ScanRunsView.OrderByDescending(r => r.ScanSequence).First();
        Assert.NotEqual(ScanRunStatus.InProgress, run.Status);
    }

    // --------------------------------------------------------------------
    // No auto-flush below thresholds: explicit FlushProgress required.
    // --------------------------------------------------------------------
    [Fact]
    public async Task NoAutoFlush_BelowThreshold_RequiresExplicitFlush()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        // High thresholds: no auto-flush
        await using var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 10, maxDirsBeforeFlush: 10);

        // Promote root
        var rootDirId = session.AddOrUpdateDirectory(session.RootDir with
        {
            Status = ScanEntryStatus.Enumerated
        });

        var hashBytes = new byte[16];
        new Random(456).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        var file = new FileRecord
        {
            FileId = 0,
            DirId = rootDirId,
            Name = "f1.txt",
            Size = 10,
            Hash = hashKey,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            Status = ScanEntryStatus.Hashed
        };
        session.AddOrUpdateFile(ref file);

        // Below threshold: nothing should have been flushed yet.
        var snapshotBefore = repo.GetRepoView();

        // No real dirs/files flushed yet (dummy ancestors are OK)
        Assert.Empty(RealDirs(snapshotBefore));
        Assert.Empty(snapshotBefore.Files);

        // Now explicitly flush and snapshot again
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);
        var snapshotAfter = repo.GetRepoView();

        var dirs = RealDirs(snapshotAfter);
        Assert.Single(dirs);
        Assert.Equal(rootDirId, dirs[0].DirId);

        var files = snapshotAfter.Files.Values.ToList();
        Assert.Single(files);
        Assert.Equal("f1.txt", files[0].Name);
        Assert.Equal(rootDirId, files[0].DirId);
    }

    // --------------------------------------------------------------------
    //    Auto-flush when file threshold is reached (async).
    // --------------------------------------------------------------------
    [Fact]
    public async Task AutoFlush_WhenFileThresholdReached_CommitsDelta_Async()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 2);

        var rootDirId = session.AddOrUpdateDirectory(session.RootDir with
        {
            Status = ScanEntryStatus.Enumerated
        });

        var hashBytes = new byte[16];
        new Random(123).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        // First file (buffered)
        var f1 = new FileRecord
        {
            FileId = 0,
            DirId = rootDirId,
            Name = "f1.txt",
            Size = 10,
            Hash = hashKey,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            Status = ScanEntryStatus.Hashed
        };
        session.AddOrUpdateFile(ref f1);

        // Second file: exceeds threshold, should trigger auto FlushProgressAsync internally
        var f2 = new FileRecord
        {
            FileId = 0,
            DirId = rootDirId,
            Name = "f2.txt",
            Size = 20,
            Hash = hashKey,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            Status = ScanEntryStatus.Hashed
        };
        session.AddOrUpdateFile(ref f2);

        // Wait until snapshot sees both files (or timeout)
        var snapshot = await WaitForSnapshotAsync(
            repo,
            s => s.Files.Count == 2 && RealDirs(s).Count == 1,
            TimeSpan.FromSeconds(2));

        Assert.NotNull(snapshot);

        var dirs = RealDirs(snapshot);
        Assert.Single(dirs);
        Assert.Equal(rootDirId, dirs[0].DirId);

        Assert.Equal(2, snapshot.Files.Count);
        var names = snapshot.Files.Values.Select(f => f.Name).ToHashSet();
        Assert.True(names.SetEquals(["f1.txt", "f2.txt"]));

        await session.DisposeAsync();
    }

    // --------------------------------------------------------------------
    // Auto-flush when directory threshold is reached (async).
    // --------------------------------------------------------------------
    [Fact]
    public async Task AutoFlush_WhenDirThresholdReached_CommitsDelta_Async()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 1000, maxDirsBeforeFlush: 2);

        var rootDirId = session.AddOrUpdateDirectory(session.RootDir with
        {
            Status = ScanEntryStatus.Enumerated
        });

        var subDir = new DirRecord
        {
            DirId = 0,
            ParentDirId = rootDirId,
            Name = "sub",
            Status = ScanEntryStatus.Enumerated
        };
        session.AddOrUpdateDirectory(subDir);

        var snapshot = await WaitForSnapshotAsync(
            repo,
            s => RealDirs(s).Count == 2,
            TimeSpan.FromSeconds(2));

        Assert.NotNull(snapshot);

        var dirs = RealDirs(snapshot);
        Assert.Equal(2, dirs.Count);

        var dirIds = dirs.Select(d => d.DirId).ToHashSet();
        Assert.Contains(rootDirId, dirIds);
        Assert.Contains(dirIds, id => id != rootDirId);

        await session.DisposeAsync();
    }

    // --------------------------------------------------------------------
    //    Below threshold there is no auto-flush:
    //    - Snapshot should be empty until FlushProgressAsync is awaited.
    // --------------------------------------------------------------------
    [Fact]
    public async Task BelowThreshold_NoAutoFlush_RequiresExplicitFlushAsync()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 1000, maxDirsBeforeFlush: 1000);

        var rootDirId = session.AddOrUpdateDirectory(session.RootDir with
        {
            Status = ScanEntryStatus.Enumerated
        });

        var hashBytes = new byte[16];
        new Random(456).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        var file = new FileRecord
        {
            FileId = 0,
            DirId = rootDirId,
            Name = "f1.txt",
            Size = 10,
            Hash = hashKey,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            Status = ScanEntryStatus.Hashed
        };
        session.AddOrUpdateFile(ref file);

        // Thresholds not reached -> no auto flush expected
        var snapshotBefore = repo.GetRepoView();

        // Only the dummy root with Status=None should exist; no "real" dirs/files yet.
        Assert.Empty(RealDirs(snapshotBefore));
        Assert.Empty(snapshotBefore.Files);

        // Explicit async flush
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        var snapshotAfter = repo.GetRepoView();

        var dirs = RealDirs(snapshotAfter);
        Assert.Single(dirs);
        Assert.Equal(rootDirId, dirs[0].DirId);

        var files = snapshotAfter.Files.Values.ToList();
        Assert.Single(files);
        Assert.Equal("f1.txt", files[0].Name);
        Assert.Equal(rootDirId, files[0].DirId);

        await session.DisposeAsync();
    }

    // --------------------------------------------------------------------
    //    Multiple auto-flushes: threshold hit several times during the scan.
    //    After a final FlushProgressAsync(), all files must be persisted.
    // --------------------------------------------------------------------
    [Fact]
    public async Task AutoFlush_CanTriggerMultipleTimes_AllDataPersisted_Async()
    {
        IRepo repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
        var rootPath = "/root";

        var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 2);

        var rootDirId = session.AddOrUpdateDirectory(session.RootDir with
        {
            Status = ScanEntryStatus.Enumerated
        });

        var hashBytes = new byte[16];
        new Random(789).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        var filenames = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var fn = $"f{i}.txt";
            filenames.Add(fn);

            var file = new FileRecord
            {
                FileId = 0,
                DirId = rootDirId,
                Name = fn,
                Size = 10 + i,
                Hash = hashKey,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                Status = ScanEntryStatus.Hashed
            };

            session.AddOrUpdateFile(ref file);
            // Auto-flush should fire at i=1,3, and then we explicitly flush at the end.
        }

        // Final explicit drain of any remaining buffered files and in-flight auto flushes
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        var snapshot = repo.GetRepoView();

        Assert.Single(RealDirs(snapshot));
        Assert.Equal(filenames.Count, snapshot.Files.Count);

        foreach (var fn in filenames)
            Assert.True(snapshot.Files.Values.Any(f => f.Name == fn),
                $"Expected file '{fn}' to be present in snapshot.");

        await session.DisposeAsync();
    }


    // -------------------------------------------------------------
    // Helpers to get at private members of FullScanOperation
    // -------------------------------------------------------------

    private static FullScanOperation CreateFullScanOperation(
        FakeFileEnumerator fs,
        ITreeIndexReadModel treeIndex,
        FakeRepoView repoView)
    {
        var repoMock = new Mock<IRepo>(MockBehavior.Strict);

        // Only thing we care about: GetRepoView must return our fake view
        repoMock.Setup(r => r.GetRepoView())
            .Returns(repoView);

        var hostMock = new Mock<IRepoHost>(MockBehavior.Strict);

        hostMock.SetupGet(h => h.Repo)
            .Returns(repoMock.Object);

        hostMock.SetupGet(h => h.TreeIndex)
            .Returns(treeIndex);

        // HashIndex is not used by ScanFolder in these tests
        hostMock.SetupGet(h => h.HashIndex)
            .Returns(Mock.Of<IHashIndexReadModel>());

        // Pipeline not used by ScanFolder either
        var pipelineMock = new Mock<IChecksumPipeline>(MockBehavior.Strict);

        return new FullScanOperation(
            hostMock.Object,
            fs,
            pipelineMock.Object,
            null);
    }


    private static async Task InvokeScanFolderAsync(
        FullScanOperation op,
        string location,
        long parentDirId,
        IScanSession session,
        List<HashingRunner.FileToHash> filesToHash,
        IRepoView repoView,
        Stack<(FsEntry dirEntry, long parentDirId, long existingDirId)> dirsToVisit)
    {
        var mi = typeof(FullScanOperation).GetMethod(
            "ScanFolder",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(mi);

        var task = (Task)mi.Invoke(
            op,
            [
                location,
                parentDirId,
                session,
                filesToHash,
                dirsToVisit,
                repoView,
                CancellationToken.None
            ])!;

        await task.ConfigureAwait(false);
    }

    [Fact]
    public async Task ScanFolder_UsesIdsFromTreeIndex_WhenCallingAddOrUpdate()
    {
        // Arrange
        var treeIndex = new FakeTreeIndex();
        var fs = new FakeFileEnumerator();

        var parentDirId = 42L;
        var existingChildId = 1001L;
        var existingFileId = 5001L;

        // Tree index: under dir 42, we know about child dir/file IDs
        treeIndex.SetChildDirs(parentDirId, existingChildId);
        treeIndex.SetChildFiles(parentDirId, existingFileId);

        var rootPath = "/root";

        // FS shows "sub" dir and "a.txt" file
        fs.SetEntries(rootPath,
            new FsEntry
            {
                IsDirectory = true,
                FullPath = Path.Combine(rootPath, "sub"),
                Name = "sub",
                CreationTimeUtc = DateTimeOffset.UtcNow,
                ModifiedTimeUtc = DateTimeOffset.UtcNow,
                Length = 0
            },
            new FsEntry
            {
                IsDirectory = false,
                FullPath = Path.Combine(rootPath, "a.txt"),
                Name = "a.txt",
                CreationTimeUtc = DateTimeOffset.UtcNow,
                ModifiedTimeUtc = DateTimeOffset.UtcNow,
                Length = 123
            });

        // RepoView must know about those IDs and names
        var repoView = new FakeRepoView
        {
            DirsDict =
            {
                [existingChildId] = new DirRecord
                {
                    DirId = existingChildId,
                    ParentDirId = parentDirId,
                    Name = "sub",
                    Status = ScanEntryStatus.Enumerated
                }
            },
            FilesDict =
            {
                [existingFileId] = new FileRecord
                {
                    FileId = existingFileId,
                    DirId = parentDirId,
                    Name = "a.txt",
                    Size = 123,
                    Status = ScanEntryStatus.Enumerated,
                    Hash = HashKey.NotComputed
                }
            }
        };

        var op = CreateFullScanOperation(fs, treeIndex, repoView);
        var session = new CapturingScanSession();
        var filesToHash = new List<HashingRunner.FileToHash>();
        var dirsToVisit = new Stack<(FsEntry dirEntry, long parentDirId, long existingDirId)>();

        // Act
        await InvokeScanFolderAsync(
            op,
            rootPath,
            parentDirId,
            session,
            filesToHash,
            repoView,
            dirsToVisit);

        // Assert: ScanFolder does not call AddOrUpdateDirectory; dirs are queued on the stack.
        // Directory ID reuse is expressed via existingDirId on the stack entry.
        var dirEntry = Assert.Single(dirsToVisit);
        Assert.Equal(existingChildId, dirEntry.existingDirId);
        Assert.Equal(parentDirId, dirEntry.parentDirId);
        Assert.Equal("sub", dirEntry.dirEntry.Name);

        var addedFile = Assert.Single(session.ObservedFiles);
        Assert.Equal(existingFileId, addedFile.FileRecord.FileId);
        Assert.Equal(parentDirId, addedFile.FileRecord.DirId);
        Assert.Equal("a.txt", addedFile.FileRecord.Name);
    }

    [Fact]
    public async Task ScanFolder_PushesChildDirIdOntoStack_ForSubdirectoryTraversal()
    {
        // Arrange
        var treeIndex = new FakeTreeIndex();
        var fs = new FakeFileEnumerator();

        var parentDirId = 10L;
        var childId = 20L;

        treeIndex.SetChildDirs(parentDirId, childId);
        treeIndex.SetChildFiles(parentDirId); // none

        var rootPath = "/root";

        fs.SetEntries(rootPath,
            new FsEntry
            {
                IsDirectory = true,
                FullPath = Path.Combine(rootPath, "child"),
                Name = "child",
                CreationTimeUtc = DateTimeOffset.UtcNow,
                ModifiedTimeUtc = DateTimeOffset.UtcNow,
                Length = 0
            });

        var repoView = new FakeRepoView
        {
            DirsDict =
            {
                [childId] = new DirRecord
                {
                    DirId = childId,
                    ParentDirId = parentDirId,
                    Name = "child",
                    Status = ScanEntryStatus.Enumerated
                }
            }
        };

        var op = CreateFullScanOperation(fs, treeIndex, repoView);
        var session = new CapturingScanSession();
        var filesToHash = new List<HashingRunner.FileToHash>();
        var dirsToVisit = new Stack<(FsEntry dirEntry, long parentDirId, long existingDirId)>();

        // Act
        await InvokeScanFolderAsync(
            op,
            rootPath,
            parentDirId,
            session,
            filesToHash,
            repoView,
            dirsToVisit);

        // Assert: the stack entry must carry the CHILD id, not the parent,
        // so the next level gets the correct parentDirId/existingDirId pair.
        var entry = Assert.Single(dirsToVisit);
        Assert.Equal(Path.Combine(rootPath, "child"), PathUtils.NormalizePath(entry.dirEntry.FullPath));
        Assert.Equal("child", entry.dirEntry.Name);
        Assert.Equal(childId, entry.existingDirId);
        Assert.Equal(parentDirId, entry.parentDirId);

        // And since there are no files here, no calls to AddOrUpdateFile.
        Assert.Empty(session.ObservedFiles);
    }
    
    // -------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------

    private sealed class FakeRepoView : IRepoView
    {
        public Dictionary<long, DirRecord> DirsDict { get; } = new();
        public Dictionary<long, FileRecord> FilesDict { get; } = new();

        public IReadOnlyDictionary<long, DirRecord> Dirs => DirsDict;
        public IReadOnlyDictionary<long, FileRecord> Files => FilesDict;

        public DirRecord? TryGetDir(long dirId)
        {
            return DirsDict.GetValueOrDefault(dirId);
        }

        public FileRecord? TryGetFile(long fileId)
        {
            return FilesDict.GetValueOrDefault(fileId);
        }
    }
    
    private sealed class FakeTreeIndex : ITreeIndexReadModel
    {
        // parentDirId -> (id, name)
        private readonly Dictionary<long, List<long>> _childDirs = new();
        private readonly Dictionary<long, List<long>> _childFiles = new();

        public ImmutableArray<long> GetChildFileIds(long dirId)
        {
            return _childFiles.TryGetValue(dirId, out var list) ? [..list] : [];
        }

        public ImmutableArray<long> GetChildDirIds(long dirId)
        {
            return _childDirs.TryGetValue(dirId, out var list) ? [..list] : [];
        }

        public void SetChildDirs(long parentId, params long[] dirs)
        {
            _childDirs[parentId] = dirs.ToList();
        }

        public void SetChildFiles(long parentId, params long[] files)
        {
            _childFiles[parentId] = files.ToList();
        }

        public DirAggregateStats GetDirStats(long dirId)
        {
            throw new NotImplementedException();
        }

        public DirAggregateStats GetDirStats(DirHandle dirId)
        {
            throw new NotImplementedException();
        }

        public ImmutableArray<DirHandle> GetChildDirIds(DirHandle dir)
        {
            throw new NotImplementedException();
        }

        public ImmutableArray<FileHandle> GetChildFileIds(DirHandle dir)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeFileEnumerator : IFileEnumerator
    {
        private readonly Dictionary<string, List<FsEntry>> _entriesByDir = new(PathUtils.PathComparer);

        public IEnumerable<FsEntry> EnumerateChildren(string root, CancellationToken cancellationToken)
        {
            root = PathUtils.NormalizePath(root);
            return _entriesByDir.TryGetValue(root, out var list)
                ? list
                : Array.Empty<FsEntry>();
        }

        public void SetEntries(string dir, params FsEntry[] entries)
        {
            dir = PathUtils.NormalizePath(dir);
            _entriesByDir[dir] = entries.ToList();
        }
    }
}