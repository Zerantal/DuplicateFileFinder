using System.Text.Json;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace RepoCompareTool;

public sealed class SemanticComparisonResult
{
    public bool SemanticallyIdentical => Differences.Count == 0;
    public List<string> Differences { get; } = [];
}

public static class SemanticRepoComparer
{
    // ANSI colors
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[37m";
    private const string Reset = "\u001b[0m";

    public static SemanticComparisonResult Compare(
        IRepo repoA, string repoPathA,
        IRepo repoB, string repoPathB)
    {
        var diff = new SemanticComparisonResult();

        var snapA = repoA.GetRepoSnapshotView();
        var snapB = repoB.GetRepoSnapshotView();

        CompareMeta(repoPathA, repoPathB, diff);
        CompareScanRoots(repoA, repoB, diff);
        // CompareHashIndex(repoA, snapA, repoB, snapB, diff);
        CompareDirs(snapA, snapB, diff);
        CompareFiles(snapA, snapB, diff);


        return diff;
    }

    // ------------------------------
    // RepoMeta (SchemaVersion, Generation, RepoPath, RepoHostName)
    // ------------------------------
    private static void CompareMeta(
        string repoPathA,
        string repoPathB,
        SemanticComparisonResult diff)
    {
        var metaA = LoadMeta(repoPathA);
        var metaB = LoadMeta(repoPathB);

        if (metaA is null && metaB is null)
            return;

        var section = new List<string>();

        if (metaA is null || metaB is null)
        {
            section.Add(SideBySide(
                "Meta",
                metaA is null ? "<missing>" : "present",
                metaB is null ? "<missing>" : "present"));
        }
        else
        {
            CompareField("SchemaVersion",
                metaA.SchemaVersion.ToString(),
                metaB.SchemaVersion.ToString(),
                section);

            CompareField("Generation",
                metaA.Generation.ToString(),
                metaB.Generation.ToString(),
                section);

            CompareField("RepoPath",
                metaA.RepoPath,
                metaB.RepoPath,
                section);

            CompareField("RepoHostName",
                metaA.RepoHostName,
                metaB.RepoHostName,
                section);
        }

        if (section.Count > 0)
        {
            diff.Differences.Add($"{Yellow}META DIFFERENCES{Reset}");
            diff.Differences.AddRange(section);
            diff.Differences.Add(string.Empty);
        }
    }

    private static RepoMeta? LoadMeta(string repoPath)
    {
        var metaFile = Path.Combine(repoPath, "meta.json");
        if (!File.Exists(metaFile))
            return null;

        var json = File.ReadAllText(metaFile);
        return JsonSerializer.Deserialize<RepoMeta>(json);
    }

    // ------------------------------
    // Scan roots (root paths only)
    // ------------------------------
    private static void CompareScanRoots(
        IRepo repoA,
        IRepo repoB,
        SemanticComparisonResult diff)
    {
        var rootsA = repoA.ScanRunsView
            .Select(r => NormalizePath(r.RootPath))
            .Distinct()
            .Order()
            .ToArray();

        var rootsB = repoB.ScanRunsView
            .Select(r => NormalizePath(r.RootPath))
            .Distinct()
            .Order()
            .ToArray();

        if (rootsA.SequenceEqual(rootsB))
            return;

        var section = new List<string>();
        var all = rootsA.Union(rootsB).Order().ToArray();

        foreach (var r in all)
        {
            var inA = rootsA.Contains(r);
            var inB = rootsB.Contains(r);

            if (inA && inB)
                continue;

            var left = inA ? r : "<missing>";
            var right = inB ? r : "<missing>";
            section.Add(SideBySide("ScanRoot", left, right));
        }

        if (section.Count > 0)
        {
            diff.Differences.Add($"{Yellow}SCAN ROOTS DIFFERENCES{Reset}");
            diff.Differences.AddRange(section);
            diff.Differences.Add(string.Empty);
        }
    }

    // ------------------------------
    // DIRS: full path + ScanEntryStatus
    // ------------------------------
    private static void CompareDirs(
        RepoSnapshotView snapA,
        RepoSnapshotView snapB,
        SemanticComparisonResult diff)
    {
        var mapA = BuildDirPathMapV2(snapA);
        var mapB = BuildDirPathMapV2(snapB);

        var allPaths = mapA.Keys.Union(mapB.Keys).Order().ToArray();
        var section = new List<string>();

        foreach (var path in allPaths)
        {
            bool aIsMissing = !mapA.TryGetValue(path, out var a);
            bool bIsMissing = !mapB.TryGetValue(path, out var b);

            if (aIsMissing && bIsMissing)
                continue;

            if (aIsMissing || bIsMissing)
            {
                var left = aIsMissing ? "<missing>" : a.Status.ToString();
                var right = bIsMissing ? "<missing>" : b.Status.ToString();
                section.Add($"DIR {path}");
                section.Add(SideBySide("Status", left, right));
                section.Add(string.Empty);
                continue;
            }

            if (a.Status != b.Status)
            {
                section.Add($"DIR {path}");
                section.Add(SideBySide("Status", a.Status.ToString(), b.Status.ToString()));
                section.Add(string.Empty);
            }
        }

        if (section.Count > 0)
        {
            diff.Differences.Add($"{Yellow}DIRECTORY DIFFERENCES{Reset}");
            diff.Differences.AddRange(section);
            diff.Differences.Add(string.Empty);
        }
    }

    // ------------------------------
    // FILES: full path + Size + Hash + Created + Status
    // ------------------------------
    private static void CompareFiles(
        RepoSnapshotView snapA,
        RepoSnapshotView snapB,
        SemanticComparisonResult diff)
    {
        var dirPathsA = BuildDirIdToPathV2(snapA);
        var dirPathsB = BuildDirIdToPathV2(snapB);

        var mapA = new Dictionary<string, FileRecordV2>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, entry) in snapA.Snapshots)
        {
            var files = entry.Files;
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var dirPath = dirPathsA.TryGetValue(f.DirId, out var p) ? p : $"<missing-dir:{f.DirId}>";
                var name = f.NameStrIdx >= 0 ? entry.StringPool.GetString(f.NameStrIdx) : "";
                var fullPath = NormalizePath(Path.Combine(dirPath, name));
                mapA[fullPath] = f;
            }
        }

        var mapB = new Dictionary<string, FileRecordV2>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, entry) in snapB.Snapshots)
        {
            var files = entry.Files;
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var dirPath = dirPathsB.TryGetValue(f.DirId, out var p) ? p : $"<missing-dir:{f.DirId}>";
                var name = f.NameStrIdx >= 0 ? entry.StringPool.GetString(f.NameStrIdx) : "";
                var fullPath = NormalizePath(Path.Combine(dirPath, name));
                mapB[fullPath] = f;
            }
        }

        var allPaths = mapA.Keys.Union(mapB.Keys).Order().ToArray();
        var section = new List<string>();

        foreach (var path in allPaths)
        {
            mapA.TryGetValue(path, out var a);
            mapB.TryGetValue(path, out var b);

            if (a.Equals(default(FileRecordV2)) && b.Equals(default(FileRecordV2)))
                continue;

            var aPresent = mapA.ContainsKey(path);
            var bPresent = mapB.ContainsKey(path);

            if (!aPresent || !bPresent)
            {
                section.Add($"FILE {path}");
                section.Add(SideBySide("Exists", aPresent ? "present" : "<missing>", bPresent ? "present" : "<missing>"));
                section.Add(string.Empty);
                continue;
            }

            var hashA = HashToString(a.Hash);
            var hashB = HashToString(b.Hash);

            var createdA = a.CreatedTicks == 0 ? null : new DateTime(a.CreatedTicks, DateTimeKind.Utc).ToString("o");
            var createdB = b.CreatedTicks == 0 ? null : new DateTime(b.CreatedTicks, DateTimeKind.Utc).ToString("o");

            var sizeA = a.Size;
            var sizeB = b.Size;

            var statusA = a.Status;
            var statusB = b.Status;

            var changed = false;
            var fileSection = new List<string>();

            if (sizeA != sizeB)
            {
                changed = true;
                fileSection.Add(SideBySide("Size", sizeA.ToString(), sizeB.ToString()));
            }

            if (hashA != hashB)
            {
                changed = true;
                fileSection.Add(SideBySide("Hash", hashA, hashB));
            }

            if (!string.Equals(createdA, createdB, StringComparison.Ordinal))
            {
                changed = true;
                fileSection.Add(SideBySide("Created", createdA ?? "<null>", createdB ?? "<null>"));
            }

            if (statusA != statusB)
            {
                changed = true;
                fileSection.Add(SideBySide("Status", statusA.ToString(), statusB.ToString()));
            }

            if (!changed)
                continue;

            section.Add($"FILE {path}");
            section.AddRange(fileSection);
            section.Add(string.Empty);
        }

        if (section.Count > 0)
        {
            diff.Differences.Add($"{Yellow}FILE DIFFERENCES{Reset}");
            diff.Differences.AddRange(section);
            diff.Differences.Add(string.Empty);
        }
    }

    // ------------------------------
    // V2 path building
    // ------------------------------

    private static Dictionary<string, DirRecordV2> BuildDirPathMapV2(RepoSnapshotView snap)
    {
        var dirIdToPath = BuildDirIdToPathV2(snap);
        var map = new Dictionary<string, DirRecordV2>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, entry) in snap.Snapshots)
        {
            var dirs = entry.Dirs;
            for (int i = 0; i < dirs.Count; i++)
            {
                var d = dirs[i];
                if (!dirIdToPath.TryGetValue(d.DirId, out var p))
                    continue;

                map[NormalizePath(p)] = d;
            }
        }

        return map;
    }

    private static Dictionary<long, string> BuildDirIdToPathV2(RepoSnapshotView snap)
    {
        // Build resolver: dirId -> (scanRootId, index)
        var handleById = new Dictionary<long, DirHandle>();
        foreach (var (scanRootId, entry) in snap.Snapshots)
        {
            var dirs = entry.Dirs;
            for (int i = 0; i < dirs.Count; i++)
            {
                var dirId = dirs[i].DirId;
                if (!handleById.TryAdd(dirId, new DirHandle(scanRootId, i)))
                    throw new InvalidOperationException($"Duplicate dirId {dirId} across scan roots.");
            }
        }

        // Memoized path builder by dirId (full path)
        var memo = new Dictionary<long, string>();
        foreach (var dirId in handleById.Keys)
            _ = GetPath(dirId);

        return memo;

        string GetPath(long dirId)
        {
            if (memo.TryGetValue(dirId, out var cached))
                return cached;

            if (!handleById.TryGetValue(dirId, out var h))
                return memo[dirId] = $"<missing-dir:{dirId}>";

            var snapView = snap.Snapshots[h.ScanRootId];
            var d = snapView.Dirs[h.Index];

            // Build leaf->root segments within snapshot
            var parts = new List<string>(16);

            // name (may be empty for root dir record)
            if (d.NameStrIdx >= 0)
            {
                var name = snapView.StringPool.GetString(d.NameStrIdx);
                if (!string.IsNullOrEmpty(name))
                    parts.Add(name);
            }

            if (d.ParentDirId >= 0)
            {
                var parentPath = GetPath(d.ParentDirId);
                var full = NormalizePath(Path.Combine(parentPath, parts.Count == 0 ? "" : parts[0]));
                memo[dirId] = full;
                return full;
            }

            // reached scan-root dir: prepend VolumePath + RootPath
            var scanRoot = snap.ScanRoots[h.ScanRootId];
            AddPathSegments(parts, scanRoot.VolumePath);
            AddPathSegments(parts, scanRoot.RootPath);

            parts.Reverse();

            string fullPath;
            if (OperatingSystem.IsWindows())
                fullPath = Path.Combine(parts.ToArray());
            else
                fullPath = Path.DirectorySeparatorChar + Path.Combine(parts.ToArray());

            fullPath = NormalizePath(fullPath);

            memo[dirId] = fullPath;
            return fullPath;
        }

        static void AddPathSegments(List<string> leafToRoot, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var segs = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = segs.Length - 1; i >= 0; i--)
                leafToRoot.Add(segs[i]);
        }
    }

    // ------------------------------
    // Helpers
    // ------------------------------
    private static string NormalizePath(string p)
    {
        // Cross-platform-ish canonicalization:
        var full = Path.GetFullPath(p);
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Replace('\\', '/');
    }

    private static string HashToString(HashKey key)
        => $"{key.A:X16}{key.B:X16}";

    private static string SideBySide(string label, string left, string right)
    {
        var width = Math.Max(left.Length, right.Length);
        var leftPadded = left.PadRight(width);
        var rightPadded = right.PadRight(width);

        return $"{label,-12} {Red}{leftPadded}{Reset} | {Green}{rightPadded}{Reset}";
    }

    private static void CompareField(string label, string left, string right, List<string> section)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal))
            section.Add(SideBySide(label, left, right));
    }
}
