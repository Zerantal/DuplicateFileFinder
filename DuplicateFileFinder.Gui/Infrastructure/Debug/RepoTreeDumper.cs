// DuplicateFileFinder.Gui/Infrastructure/Debug/RepoTreeDumper.cs

using System.Text;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Infrastructure.Debug;

public static class RepoTreeDumper
{
    private static readonly Lock s_dumpLock = new();
    private const string DumpPrefix = "repo_tree_dump_";
    private const string DumpExt = ".txt";

    private static string GetNextDumpPath()
    {
        var dir = Path.Combine(App.AppDir, "dump");
        Directory.CreateDirectory(dir);

        lock (s_dumpLock)
        {
            var max = 0;

            foreach (var path in Directory.EnumerateFiles(dir, DumpPrefix + "*" + DumpExt))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(DumpPrefix, StringComparison.Ordinal) || !name.EndsWith(DumpExt, StringComparison.Ordinal))
                    continue;

                var middle = name.Substring(DumpPrefix.Length, name.Length - DumpPrefix.Length - DumpExt.Length);
                if (int.TryParse(middle, out var n) && n > max)
                    max = n;
            }

            var next = max + 1;

            // No padding required, but padding looks nice and sorts well.
            var file = $"{DumpPrefix}{next:D4}{DumpExt}";
            return Path.Combine(dir, file);
        }
    }

    public static async Task<string> DumpAsync(IRepoHost host, bool dumpLiveTreesOnly, CancellationToken ct = default)
    {
        var outputPath = GetNextDumpPath();
        await DumpAsync(host, outputPath, dumpLiveTreesOnly, ct);
        return outputPath;
    }

    public static async Task<string> DumpAsync(
        IRepoHost host,
        string outputPath,
        bool dumpLiveTreesOnly,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var sb = new StringBuilder(capacity: 256 * 1024);

        // Header
        sb.AppendLine($"DuplicateFileFinder Repo Dump");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        sb.AppendLine();

        // Get scan roots (stable order)
        var roots = host.Repo.ScanRootsView
            .Where(r => !dumpLiveTreesOnly || !r.IsDeleted)
            .OrderBy(r => r.RootId)
            .ToArray();

        if (roots.Length == 0)
        {
            sb.AppendLine("(no scan roots)");
        }
        else
        {
            // Build a lookup of (scanRootId -> bool[dirIndex]) where true means that dir participates
            // in any duplicate-folder group in FolderHashIndex.
            var dupFolderLookup = BuildDuplicateFolderLookup(host, roots, ct);

            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();

                sb.AppendLine($"ScanRoot {root.RootId}: {root.RootPath}{(root.IsDeleted ? " (deleted)" : "")}");

                var view = host.Repo.TryGetScanRootView(root.RootId);
                if (view is null)
                {
                    sb.AppendLine("  (no snapshot loaded for this scan root)");
                    sb.AppendLine();
                    continue;
                }

                dupFolderLookup.TryGetValue(root.RootId, out var isDupFolderByDirIndex);

                DumpScanRoot(view, isDupFolderByDirIndex, sb);

                sb.AppendLine();
            }
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct);
        return outputPath;
    }

    private static Dictionary<long, bool[]> BuildDuplicateFolderLookup(
        IRepoHost host,
        ScanRoot[] roots,
        CancellationToken ct)
    {
        // Lazily create arrays sized to each scan root snapshot's dir count.
        var lookup = new Dictionary<long, bool[]>(capacity: roots.Length);

        bool[] GetOrCreate(long scanRootId)
        {
            if (lookup.TryGetValue(scanRootId, out var existing))
                return existing;

            var view = host.Repo.TryGetScanRootView(scanRootId);
            if (view is null)
            {
                // If a snapshot isn't loaded, we can't size the array; just store an empty placeholder.
                existing = [];
                lookup[scanRootId] = existing;
                return existing;
            }

            existing = new bool[view.Dirs.Count];
            lookup[scanRootId] = existing;
            return existing;
        }

        // FolderHashIndex contains only duplicate groups; every handle in every group is "dup-folder".
        // We page until the index returns an empty page.
        const int PageSize = 512;
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = host.FolderHashIndex.GetGroupsPage(offset, PageSize, FolderDuplicateSort.DuplicateCountDesc);
            if (page.Count <= 0)
                break;

            var groups = page.Groups.Span;

            for (var i = 0; i < groups.Length; i++)
            {
                var g = groups[i];
                var dirs = host.FolderHashIndex.GetGroupDirs(g);

                for (var j = 0; j < dirs.Length; j++)
                {
                    var h = dirs[j];

                    // Defensive: skip invalid
                    if (!h.IsValid)
                        continue;

                    var arr = GetOrCreate(h.ScanRootId);
                    if ((uint)h.Index < (uint)arr.Length)
                        arr[h.Index] = true;
                }
            }

            // PagingList-style: when fewer than requested are returned, we're at the end.
            offset += page.Count;
            if (page.Count < PageSize)
                break;
        }

        return lookup;
    }

    private static void DumpScanRoot(ScanRootSnapshotView view, bool[]? isDupFolderByDirIndex, StringBuilder sb)
    {
        // Build indices
        var dirs = view.Dirs;
        var files = view.Files;

        // children dirs by parent id
        var childDirs = new Dictionary<long, List<int>>();
        for (int i = 0; i < dirs.Count; i++)
        {
            var parentId = dirs[i].ParentDirId;
            if (parentId < 0) continue;

            if (!childDirs.TryGetValue(parentId, out var list))
            {
                list = new List<int>();
                childDirs[parentId] = list;
            }
            list.Add(i);
        }

        // files by dirId
        var filesByDir = new Dictionary<long, List<int>>();
        for (int i = 0; i < files.Count; i++)
        {
            var dirId = files[i].DirId;
            if (!filesByDir.TryGetValue(dirId, out var list))
            {
                list = new List<int>();
                filesByDir[dirId] = list;
            }
            list.Add(i);
        }

        // duplicates: file hash -> count (ignore Deleted by default)
        var dupCounts = BuildDuplicateCounts(view);

        // Root dir record(s)
        var rootIndices = Enumerable.Range(0, dirs.Count)
            .Where(i => dirs[i].ParentDirId < 0)
            .ToArray();

        if (rootIndices.Length == 0)
        {
            sb.AppendLine("  (snapshot has no root dir records)");
            return;
        }

        foreach (var rootIdx in rootIndices.OrderBy(i => SafeDirName(view, dirs[i])))
        {
            var rootDir = dirs[rootIdx];
            var rootName = SafeDirName(view, rootDir);

            sb.AppendLine($"  {rootName}{FormatDeleted(rootDir.Status)}{FormatDupFolder(isDupFolderByDirIndex, rootIdx)}");

            DumpDirRecursive(
                view,
                rootDir.DirId,
                indent: "  ",
                childDirs,
                filesByDir,
                dupCounts,
                isDupFolderByDirIndex,
                sb);
        }
    }

    private static Dictionary<string, int> BuildDuplicateCounts(ScanRootSnapshotView view)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var f in view.Files)
        {
            if (f.Status == ScanEntryStatus.Deleted)
                continue;

            if (!f.Hash.IsComputed)
                continue;

            var key = f.Hash.ToString();
            dict.TryGetValue(key, out var n);
            dict[key] = n + 1;
        }

        return dict;
    }

    private static void DumpDirRecursive(
        ScanRootSnapshotView view,
        long dirId,
        string indent,
        Dictionary<long, List<int>> childDirs,
        Dictionary<long, List<int>> filesByDir,
        Dictionary<string, int> dupCounts,
        bool[]? isDupFolderByDirIndex,
        StringBuilder sb)
    {
        // For children: directories first, then files (like `tree`)
        childDirs.TryGetValue(dirId, out var dirChildren);
        filesByDir.TryGetValue(dirId, out var fileChildren);

        dirChildren ??= [];
        fileChildren ??= [];

        // Sort by name
        dirChildren.Sort((a, b) => string.CompareOrdinal(
            SafeDirName(view, view.Dirs[a]),
            SafeDirName(view, view.Dirs[b])));

        fileChildren.Sort((a, b) => string.CompareOrdinal(
            SafeFileName(view, view.Files[a]),
            SafeFileName(view, view.Files[b])));

        // Determine which files in *this folder* are duplicates (immediate only)
        var folderHasDupFiles = fileChildren.Any(i =>
        {
            var f = view.Files[i];
            if (f.Status == ScanEntryStatus.Deleted || !f.Hash.IsComputed)
                return false;
            var k = f.Hash.ToString();
            return dupCounts.TryGetValue(k, out var c) && c > 1;
        });

        // Emit folders + files
        var total = dirChildren.Count + fileChildren.Count;
        for (int n = 0; n < total; n++)
        {
            var isDir = n < dirChildren.Count;
            var childIsLast = (n == total - 1);

            var branch = childIsLast ? "└── " : "├── ";
            var nextIndent = indent + (childIsLast ? "    " : "│   ");

            if (isDir)
            {
                var idx = dirChildren[n];
                var d = view.Dirs[idx];

                var name = SafeDirName(view, d);
                var flags = FormatDeleted(d.Status);

                // Folder markers:
                // - dup-folder: folder content signature matches at least one other folder across dumped roots
                // - has-dup-files: immediate duplicate files exist in this folder
                var dupFolderFlag = FormatDupFolder(isDupFolderByDirIndex, idx);
                var hasDupFilesFlag = folderHasDupFiles ? " (has-dup-files)" : string.Empty;

                sb.Append(indent).Append(branch).Append(name).Append(flags).Append(dupFolderFlag).AppendLine(hasDupFilesFlag);

                DumpDirRecursive(
                    view,
                    d.DirId,
                    nextIndent,
                    childDirs,
                    filesByDir,
                    dupCounts,
                    isDupFolderByDirIndex,
                    sb);
            }
            else
            {
                var idx = fileChildren[n - dirChildren.Count];
                var f = view.Files[idx];

                var name = SafeFileName(view, f);
                var flags = FormatDeleted(f.Status);

                var dup = string.Empty;
                if (f.Status != ScanEntryStatus.Deleted && f.Hash.IsComputed)
                {
                    var k = f.Hash.ToString();
                    if (dupCounts.TryGetValue(k, out var c) && c > 1)
                        dup = " (dup)";
                }

                sb.Append(indent).Append(branch).Append(name).Append(flags).AppendLine(dup);
            }
        }
    }

    private static string FormatDupFolder(bool[]? isDupFolderByDirIndex, int dirIndex)
    {
        if (isDupFolderByDirIndex is null)
            return string.Empty;

        if ((uint)dirIndex >= (uint)isDupFolderByDirIndex.Length)
            return string.Empty;

        return isDupFolderByDirIndex[dirIndex] ? " (dup-folder)" : string.Empty;
    }

    private static string SafeDirName(ScanRootSnapshotView view, DirRecordV2 d)
    {
        if (d.ParentDirId < 0)
            return "(root)";

        if (d.NameStrIdx < 0)
            return $"(dir {d.DirId})";
        var s = view.StringPool.GetString(d.NameStrIdx);
        return string.IsNullOrWhiteSpace(s) ? $"(dir {d.DirId})" : s;
    }

    private static string SafeFileName(ScanRootSnapshotView view, FileRecordV2 f)
    {
        if (f.NameStrIdx < 0)
            return $"(file {f.FileId})";
        var s = view.StringPool.GetString(f.NameStrIdx);
        return string.IsNullOrWhiteSpace(s) ? $"(file {f.FileId})" : s;
    }

    private static string FormatDeleted(ScanEntryStatus status)
        => status == ScanEntryStatus.Deleted ? " (deleted)" : string.Empty;
}
