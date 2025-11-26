// DuplicateFileFinderLibTests/Repository/RepoScanRootMigrationTests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Migrations;

public sealed class RepoScanRootMigrationTests
{
    [Fact]
    public void Open_OldSchema_WithoutScanRoots_MigratesToScanRoots()
    {
        TempFsFixture tempDir = new("repo");
             
        try
        {
         // 1. Prepare minimal old-style repo on disk
         var repoPath = tempDir.Root;

         // Old meta with SchemaVersion=4 and no NextScanSequence yet
         var oldMeta = new RepoMeta
         {
             SchemaVersion            = 4,
             Generation               = 1,
             NextLogSequence          = 0,
             LastSnapshottedLogSequence = -1,
             LastCompaction           = DateTimeOffset.UtcNow,
             RepoId                   = Guid.NewGuid(),
             RepoPath                 = repoPath,
             RepoHostName             = Environment.MachineName,
             NextScanSequence         = 1
         };

         // 2. Write old-style scanruns.json (no ScanRootId, no ScanRoots file)
         var runs = new List<ScanRun>
         {
             new()
             {
                 ScanSequence = 1,
                 // ScanRootId will be Guid.Empty when deserialized because the property didn't exist
                 RootPath     = "/mnt/data/rootA",
                 StartedAt    = DateTimeOffset.UtcNow.AddMinutes(-10),
                 FinishedAt   = DateTimeOffset.UtcNow.AddMinutes(-9),
                 Status       = ScanRunStatus.Completed,
                 ErrorMessage = null,
                 Mode         = ScanMode.Full
             },
             new()
             {
                 ScanSequence = 2,
                 RootPath     = "/mnt/data/rootB",
                 StartedAt    = DateTimeOffset.UtcNow.AddMinutes(-5),
                 FinishedAt   = DateTimeOffset.UtcNow.AddMinutes(-4),
                 Status       = ScanRunStatus.Completed,
                 ErrorMessage = null,
                 Mode         = ScanMode.Full
             }
         };
             
         File.WriteAllBytes(Path.Combine(repoPath, "snapshot.bin"),
             MemoryPack.MemoryPackSerializer.Serialize(new RepoSnapshot
             {
                 Files = new Dictionary<Guid, FileRecord>(),
                 Dirs  = new Dictionary<Guid, DirRecord>(),
                 HashIndex = new Dictionary<HashKey, List<Guid>>(),
                 Meta = oldMeta,
                 ScanRuns = runs
             }));

         File.WriteAllText(Path.Combine(repoPath, "meta.json"),
             JsonSerializer.Serialize(oldMeta, new JsonSerializerOptions { WriteIndented = true }));

  

         var scanRunsFile = Path.Combine(repoPath, "scanruns.json");
         File.WriteAllText(scanRunsFile,
             JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));

         // 3. Open repo (this should trigger migration)
         var repo = Repo.Open(repoPath);

         // 4. Assert: SchemaVersion bumped, ScanRoots created, ScanRuns patched
         var roots = repo.ScanRootsView;
         var scanRuns = repo.ScanRunsView;

         Assert.True(repo.ScanRootsView.Count >= 2);
         Assert.Equal(5, repo.Meta.SchemaVersion); 

         // Distinct canonical paths => distinct roots
         var canonicalA = PathUtils.NormalizePath("/mnt/data/rootA");
         var canonicalB = PathUtils.NormalizePath("/mnt/data/rootB");

         var rootA = roots.Single(r => r.RootPath == canonicalA);
         var rootB = roots.Single(r => r.RootPath == canonicalB);

         // Each run must now have a non-empty ScanRootId referencing those roots
         var run1 = scanRuns.Single(r => r.ScanSequence == 1);
         var run2 = scanRuns.Single(r => r.ScanSequence == 2);

         Assert.NotEqual(Guid.Empty, run1.ScanRootId);
         Assert.NotEqual(Guid.Empty, run2.ScanRootId);
         Assert.NotEqual(run1.ScanRootId, run2.ScanRootId);

         Assert.Equal(rootA.Id, run1.ScanRootId);
         Assert.Equal(rootB.Id, run2.ScanRootId);
        }
        finally
        {
            tempDir.Dispose();
        }
    }

    [Fact]
    public void Open_NewSchema_WithScanRoots_DoesNotDuplicateRoots()
    {
        TempFsFixture tempDir = new("repo");
        
        try
        {
            var repoPath = tempDir.Root;
        
            // Create a fresh repo via Repo.Open then close
            Repo.Open(repoPath);
        
            // Simulate one root + run
            var root = new ScanRoot
            {
                Id       = Guid.NewGuid(),
                RootPath = PathUtils.NormalizePath("/mnt/data/rootC"),
                DirId    = Guid.Empty,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var run = new ScanRun
            {
                ScanSequence = 1,
                ScanRootId   = root.Id,
                RootPath     = root.RootPath,
                StartedAt    = DateTimeOffset.UtcNow,
                FinishedAt   = null,
                Status       = ScanRunStatus.InProgress
            };
        
            var rootsDict = new Dictionary<Guid, ScanRoot> { [root.Id] = root };
            // Persist as if it was already migrated
            File.WriteAllText(Path.Combine(repoPath, "scanroots.json"),
                JsonSerializer.Serialize(rootsDict, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(repoPath, "scanruns.json"),
                JsonSerializer.Serialize(new[] { run }, new JsonSerializerOptions { WriteIndented = true }));
        
            // Update meta to new schema version
            var metaPath = Path.Combine(repoPath, "meta.json");
            var metaJson = File.ReadAllText(metaPath);
            var meta = JsonSerializer.Deserialize<RepoMeta>(metaJson)!;
            meta = meta with { SchemaVersion = 5 };
            File.WriteAllText(metaPath,
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        
            // Re-open: migration should be a no-op
            var repo2 = Repo.Open(repoPath);
        
            Assert.Single(repo2.ScanRootsView);
            Assert.Single(repo2.ScanRunsView);
        
            Assert.Equal(root.RootPath, repo2.ScanRootsView[0].RootPath);
            Assert.Equal(root.Id, repo2.ScanRunsView[0].ScanRootId);
        }
        finally
        {
            tempDir.Dispose();
        }
    }
}