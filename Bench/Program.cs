using Bench;

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Storage.Models;

using NLog;

var log = LogManager.GetCurrentClassLogger();

TimingLog.AddCounterFormatter("bytes_hashed", n => n.ToSizeString());

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var mode = args[0];

switch (mode)
{
    case "--scan":
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 1;
        }

        var root = Path.GetFullPath(args[1]);

        var repoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bench",
            "repo");

        if (Directory.Exists(repoDir))
            Directory.Delete(repoDir, recursive: true);

        var host = await RepoHost.OpenAsync(repoDir);
        try
        {
            var finder = new DuplicateFileFinder(host);

            log.Info("Bench location: {root}", root);
            using (TimingLog.Start("Folder scan", root))
            {
                await finder.FullScanAsync(root);
            }
        }
        finally
        {
            await host.DisposeAsync();
        }

        return 0;
    }

    case "--delete-dir":
    {
        if (args.Length < 3)
        {
            PrintUsage();
            return 1;
        }

        var repoDir = Path.GetFullPath(args[1]);
        var dirPath = Path.GetFullPath(args[2]);

        var host = await RepoHost.OpenAsync(repoDir);
        try
        {
            log.Info("Bench repo: {repoDir}", repoDir);
            log.Info("Delete dir target: {dirPath}", dirPath);

            var dirHandle = TryResolveDirHandle(host, dirPath);
            if (!dirHandle.IsValid)
            {
                log.Error("Could not resolve repo handle for directory: {dirPath}", dirPath);
                return 2;
            }

            DeleteResult result;
            using (TimingLog.Start("Delete dir from repo", dirPath))
            {
                result = await host.Repo.DeleteDirAsync(dirHandle);
            }

            if (!result.Success)
            {
                log.Error("Repo.DeleteDirAsync failed: {error}", result.Error);
                return 3;
            }

            log.Info(
                "Delete-dir committed. Generation={generation}, DeletedDirs={deletedDirs}, DeletedFiles={deletedFiles}",
                result.Generation,
                result.DeletedDirCount,
                result.DeletedFileCount);

            using (TimingLog.Start("Wait for indexes rebuilt", result.Generation.ToString()))
            {
                await host.WhenIndexesRebuiltAsync(result.Generation);
            }

            log.Info("Indexes rebuilt through generation {generation}", result.Generation);
        }
        finally
        {
            await host.DisposeAsync();
        }

        return 0;
    }

    case "--delete-file":
    {
        if (args.Length < 3)
        {
            PrintUsage();
            return 1;
        }

        var repoDir = Path.GetFullPath(args[1]);
        var filePath = Path.GetFullPath(args[2]);

        var host = await RepoHost.OpenAsync(repoDir);
        try
        {
            log.Info("Bench repo: {repoDir}", repoDir);
            log.Info("Delete file target: {filePath}", filePath);

            var fileHandle = TryResolveFileHandle(host, filePath);
            if (!fileHandle.IsValid)
            {
                log.Error("Could not resolve repo handle for file: {filePath}", filePath);
                return 2;
            }

            DeleteResult result;
            using (TimingLog.Start("Delete file from repo", filePath))
            {
                result = await host.Repo.DeleteFileAsync(fileHandle);
            }

            if (!result.Success)
            {
                log.Error("Repo.DeleteFileAsync failed: {error}", result.Error);
                return 3;
            }

            log.Info(
                "Delete-file committed. Generation={generation}, DeletedDirs={deletedDirs}, DeletedFiles={deletedFiles}",
                result.Generation,
                result.DeletedDirCount,
                result.DeletedFileCount);

            using (TimingLog.Start("Wait for indexes rebuilt", result.Generation.ToString()))
            {
                await host.WhenIndexesRebuiltAsync(result.Generation);
            }

            log.Info("Indexes rebuilt through generation {generation}", result.Generation);
        }
        finally
        {
            await host.DisposeAsync();
        }

        return 0;
    }

    default:
        PrintUsage();
        return 1;
}

static DirHandle TryResolveDirHandle(RepoHost host, string dirPath)
{
    var normalizedTarget = NormalizeForCompare(dirPath);

    foreach (var scanRoot in host.Repo.ScanRootsView.Where(r => !r.IsDeleted))
    {
        var rootPath = NormalizeForCompare(ResolveScanRootPath(scanRoot));

        if (string.Equals(rootPath, normalizedTarget, StringComparison.Ordinal))
            return new DirHandle(scanRoot.RootId, 0);

        if (!normalizedTarget.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            continue;

        var relative = normalizedTarget[(rootPath.Length + 1)..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var snap = host.Repo.TryGetScanRootView(scanRoot.RootId);
        if (snap is null)
            continue;

        var handle = TryResolveDirHandleInSnapshot(scanRoot.RootId, snap, segments);
        if (handle.IsValid)
            return handle;
    }

    return DirHandle.Invalid;
}

static FileHandle TryResolveFileHandle(RepoHost host, string filePath)
{
    var normalizedTarget = NormalizeForCompare(filePath);

    foreach (var scanRoot in host.Repo.ScanRootsView.Where(r => !r.IsDeleted))
    {
        var rootPath = NormalizeForCompare(ResolveScanRootPath(scanRoot));

        if (!normalizedTarget.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            continue;

        var relative = normalizedTarget[(rootPath.Length + 1)..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            continue;

        var snap = host.Repo.TryGetScanRootView(scanRoot.RootId);
        if (snap is null)
            continue;

        var handle = TryResolveFileHandleInSnapshot(scanRoot.RootId, snap, segments);
        if (handle.IsValid)
            return handle;
    }

    return FileHandle.Invalid;
}

static DirHandle TryResolveDirHandleInSnapshot(
    ScanRootId scanRootId,
    ScanRootSnapshotView snap,
    IReadOnlyList<string> segments)
{
    if (segments.Count == 0)
        return new DirHandle(scanRootId, 0);

    var currentIndex = 0;
    for (var s = 0; s < segments.Count; s++)
    {
        var currentDir = snap.Dirs[currentIndex];
        var wanted = segments[s];

        var foundChildIndex = -1;

        for (var i = 0; i < snap.Dirs.Count; i++)
        {
            var candidate = snap.Dirs[i];
            if (candidate.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                continue;

            if (candidate.ParentDirId != currentDir.DirId)
                continue;

            var name = snap.StringPool.GetString(candidate.NameStrIdx);
            if (!string.Equals(name, wanted, StringComparison.Ordinal))
                continue;

            foundChildIndex = i;
            break;
        }

        if (foundChildIndex < 0)
            return DirHandle.Invalid;

        currentIndex = foundChildIndex;
    }

    return new DirHandle(scanRootId, currentIndex);
}

static FileHandle TryResolveFileHandleInSnapshot(
    ScanRootId scanRootId,
    ScanRootSnapshotView snap,
    IReadOnlyList<string> segments)
{
    if (segments.Count == 0)
        return FileHandle.Invalid;

    var dirSegments = segments.Count == 1 ? Array.Empty<string>() : segments.Take(segments.Count - 1).ToArray();
    var fileName = segments[^1];

    var dirHandle = TryResolveDirHandleInSnapshot(scanRootId, snap, dirSegments);
    if (!dirHandle.IsValid)
        return FileHandle.Invalid;

    var dirRecord = snap.Dirs[dirHandle.Index];

    for (var i = 0; i < snap.Files.Count; i++)
    {
        var candidate = snap.Files[i];
        if (candidate.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
            continue;

        if (candidate.DirId != dirRecord.DirId)
            continue;

        var name = snap.StringPool.GetString(candidate.NameStrIdx);
        if (!string.Equals(name, fileName, StringComparison.Ordinal))
            continue;

        return new FileHandle(scanRootId, i);
    }

    return FileHandle.Invalid;
}

static string ResolveScanRootPath(ScanRoot scanRoot)
{
    var rootPath = scanRoot.RootPath;

    if (!string.IsNullOrWhiteSpace(scanRoot.VolumePath) && !Path.IsPathRooted(rootPath))
        rootPath = Path.Combine(scanRoot.VolumePath!, rootPath);

    return Path.GetFullPath(rootPath);
}

static string NormalizeForCompare(string path) =>
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  bench --scan <rootPath>");
    Console.WriteLine("  bench --delete-dir <repoDir> <dirPath>");
    Console.WriteLine("  bench --delete-file <repoDir> <filePath>");
}
