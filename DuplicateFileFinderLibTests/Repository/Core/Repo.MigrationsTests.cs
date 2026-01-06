using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core;

// ReSharper disable once InconsistentNaming
public sealed class Repo_MigrationsTests
{
    [Fact]
    public async Task OpenAsync_NormalizesSchemaVersionInMemory_EvenIfMetaOnDiskIsOld()
    {
        var repoDir = CreateTempDir();
        try
        {
            // Write an "old schema" meta file to disk
            Directory.CreateDirectory(repoDir);

            var metaFile = new RepoMetaFile
            {
                Meta = new RepoMeta
                {
                    SchemaVersion = 1,
                    Generation = 1,
                    RepoId = Guid.NewGuid(),
                    RepoPath = repoDir,
                    RepoHostName = Environment.MachineName,
                    NextScanSequence = 0,
                    NextScanRootId = 1,
                    NextDirId = 1,
                    NextFileId = 1
                },
                ScanRoots = new(),
                ScanRuns = new()
            };

            await RepoStore.SaveMetaAsync(repoDir, metaFile, CancellationToken.None);

            // Confirm on-disk schema is old
            var disk0 = await RepoStore.LoadMetaAsync(repoDir, CancellationToken.None);
            Assert.NotNull(disk0);
            Assert.Equal(1, disk0.Meta.SchemaVersion);

            // Open repo: LoadFromMetaFile should normalize Meta.SchemaVersion in memory
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);

            var expected = GetRepoSchemaVersion();
            var meta = (RepoMeta)typeof(Repo)
                .GetField("_meta", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(repo)!;
            Assert.Equal(expected, meta.SchemaVersion);


            // Still old on disk (normalization doesn't mark meta dirty)
            var disk1 = await RepoStore.LoadMetaAsync(repoDir, CancellationToken.None);
            Assert.NotNull(disk1);
            Assert.Equal(1, disk1.Meta.SchemaVersion);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task OldSchema_IsPersistedAsCurrent_OnNextMetaPersist()
    {
        var repoDir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(repoDir);

            var metaFile = new RepoMetaFile
            {
                Meta = new RepoMeta
                {
                    SchemaVersion = 1,
                    Generation = 1,
                    RepoId = Guid.NewGuid(),
                    RepoPath = repoDir,
                    RepoHostName = Environment.MachineName,
                    NextScanSequence = 0,
                    NextScanRootId = 1,
                    NextDirId = 1,
                    NextFileId = 1
                },
                ScanRoots = new(),
                ScanRuns = new()
            };

            await RepoStore.SaveMetaAsync(repoDir, metaFile, CancellationToken.None);

            // Open repo and trigger a meta persist (BeginScan marks meta dirty and persists)
            await using (var repo = await Repo.OpenAsync(repoDir, CancellationToken.None))
            {
                var internalRepo = (IRepoInternal)repo;

                _ = await internalRepo.BeginNewScanAsync(
                    rootPath: Path.Combine(repoDir, "root"),
                    options: new ScanOptions(StartFresh: true),
                    volumeInfo: null,
                    ct: CancellationToken.None);
            }

            // Now disk should reflect current schema
            var disk = await RepoStore.LoadMetaAsync(repoDir, CancellationToken.None);
            Assert.NotNull(disk);

            var expected = GetRepoSchemaVersion();
            Assert.Equal(expected, disk.Meta.SchemaVersion);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    private static int GetRepoSchemaVersion()
    {
        // Prefer const field if present
        var f = typeof(Repo).GetField("RepoSchemaVersion",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(f);
        return (int)f.GetValue(null)!;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dff_repo_mig_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        { Directory.Delete(dir, recursive: true); }
        catch { /* ignore */ }
    }
}
