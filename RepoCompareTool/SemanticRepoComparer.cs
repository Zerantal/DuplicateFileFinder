using System.Text.Json;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace RepoCompareTool;

public sealed class SemanticComparisonResult
{
    public bool SemanticallyIdentical => Differences.Count == 0;
    public List<string> Differences { get; } = new();
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

        var snapA = repoA.GetRepoView();
        var snapB = repoB.GetRepoView();

        CompareMeta(repoPathA, repoPathB, diff);
        CompareScanRoots(repoA, repoB, diff);
        CompareDirs(repoA, snapA, repoB, snapB, diff);
        CompareFiles(repoA, snapA, repoB, snapB, diff);
        // CompareHashIndex(repoA, snapA, repoB, snapB, diff);

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
        IRepo repoA, IRepoView snapA,
        IRepo repoB, IRepoView snapB,
        SemanticComparisonResult diff)
    {
        var mapA = snapA.Dirs.Values
            .ToDictionary(
                d => NormalizePath(repoA.GetDirPath(d.DirId)),
                d => d);

        var mapB = snapB.Dirs.Values
            .ToDictionary(
                d => NormalizePath(repoB.GetDirPath(d.DirId)),
                d => d);

        var allPaths = mapA.Keys.Union(mapB.Keys).Order().ToArray();

        var section = new List<string>();

        foreach (var path in allPaths)
        {
            mapA.TryGetValue(path, out var a);
            mapB.TryGetValue(path, out var b);

            if (a is null && b is null)
                continue;

            if (a is null || b is null)
            {
                var left = a is null ? "<missing>" : a.Status.ToString();
                var right = b is null ? "<missing>" : b.Status.ToString();
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
        IRepo repoA, IRepoView snapA,
        IRepo repoB, IRepoView snapB,
        SemanticComparisonResult diff)
    {
        var dirPathsA = snapA.Dirs.Values
            .ToDictionary(d => d.DirId, d => NormalizePath(repoA.GetDirPath(d.DirId)));

        var dirPathsB = snapB.Dirs.Values
            .ToDictionary(d => d.DirId, d => NormalizePath(repoB.GetDirPath(d.DirId)));

        var mapA = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapA.Files.Values)
        {
            var dirPath = dirPathsA.TryGetValue(f.DirId, out var p) ? p : $"<missing-dir:{f.DirId}>";
            var fullPath = NormalizePath(Path.Combine(dirPath, f.Name));
            mapA[fullPath] = f;
        }

        var mapB = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snapB.Files.Values)
        {
            var dirPath = dirPathsB.TryGetValue(f.DirId, out var p) ? p : $"<missing-dir:{f.DirId}>";
            var fullPath = NormalizePath(Path.Combine(dirPath, f.Name));
            mapB[fullPath] = f;
        }

        var allPaths = mapA.Keys.Union(mapB.Keys).Order().ToArray();

        var section = new List<string>();

        foreach (var path in allPaths)
        {
            mapA.TryGetValue(path, out var a);
            mapB.TryGetValue(path, out var b);

            if (a is null && b is null)
                continue;

            if (a is null || b is null)
            {
                section.Add($"FILE {path}");
                var left = a is null ? "<missing>" : "present";
                var right = b is null ? "<missing>" : "present";
                section.Add(SideBySide("Exists", left, right));
                section.Add(string.Empty);
                continue;
            }

            var hashA = HashToString(a.Hash);
            var hashB = HashToString(b.Hash);

            var createdA = a.Created?.ToUniversalTime().ToString("o");
            var createdB = b.Created?.ToUniversalTime().ToString("o");

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

            if (createdA != createdB)
            {
                changed = true;
                fileSection.Add(SideBySide("Created", createdA!, createdB!));
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
    // HASH INDEX: hash -> sorted full paths
    // ------------------------------
    // private static void CompareHashIndex(
    //     IRepo repoA, RepoViewSnapshot snapA,
    //     IRepo repoB, RepoViewSnapshot snapB,
    //     SemanticComparisonResult diff)
    // {
    //     IHashIndexService hashIndexService = new  HashIndexService();
    //     
    //     var dirPathsA = snapA.Dirs.Values
    //         .ToDictionary(d => d.DirId, d => NormalizePath(repoA.GetDirPath(d.DirId)));
    //
    //     var dirPathsB = snapB.Dirs.Values
    //         .ToDictionary(d => d.DirId, d => NormalizePath(repoB.GetDirPath(d.DirId)));
    //
    //     // Build semantic hash -> paths map for A
    //     var indexA = hashIndexService.BuildIndex(snapA)
    //         .GroupBy(kv => HashToString(kv.Key))
    //         .ToDictionary(
    //             g => g.Key,
    //             g => g.SelectMany(kv => kv.Value.Select(fid =>
    //                 {
    //                     var f = snapA.Files[fid];
    //                     var dirPath = dirPathsA.TryGetValue(f.DirId, out var p) ? p : $"<missing-dir:{f.DirId}>";
    //                     return NormalizePath(Path.Combine(dirPath, f.Name));
    //                 }))
    //                 .Distinct()
    //                 .OrderBy(p => p)
    //                 .ToArray()
    //         );
    //
    //     // Build semantic hash -> paths map for B
    //     var indexB = hashIndexService.BuildIndex(snapB)
    //         .GroupBy(kv => HashToString(kv.Key))
    //         .ToDictionary(
    //             g => g.Key,
    //             g => g.SelectMany(kv => kv.Value.Select(fid =>
    //                 {
    //                     var f = snapB.Files[fid];
    //                     var dirPath = dirPathsB.TryGetValue(f.DirId, out var p) ? p : $"<missing-dir:{f.DirId}>";
    //                     return NormalizePath(Path.Combine(dirPath, f.Name));
    //                 }))
    //                 .Distinct()
    //                 .OrderBy(p => p)
    //                 .ToArray()
    //         );
    //
    //     var section = new List<string>();
    //
    //     var keysA = indexA.Keys.OrderBy(k => k).ToArray();
    //     var keysB = indexB.Keys.OrderBy(k => k).ToArray();
    //
    //     if (!keysA.SequenceEqual(keysB))
    //     {
    //         var aOnly = keysA.Except(keysB).ToArray();
    //         var bOnly = keysB.Except(keysA).ToArray();
    //
    //         foreach (var h in aOnly)
    //             section.Add(SideBySide("Hash", h, "<missing>"));
    //
    //         foreach (var h in bOnly)
    //             section.Add(SideBySide("Hash", "<missing>", h));
    //     }
    //     else
    //     {
    //         foreach (var hash in keysA)
    //         {
    //             var pathsA = indexA[hash];
    //             var pathsB = indexB[hash];
    //
    //             if (pathsA.SequenceEqual(pathsB))
    //                 continue;
    //
    //             var left = string.Join(", ", pathsA);
    //             var right = string.Join(", ", pathsB);
    //
    //             section.Add($"HASH {hash}");
    //             section.Add(SideBySide("Paths", left, right));
    //             section.Add(string.Empty);
    //         }
    //     }
    //
    //     if (section.Count > 0)
    //     {
    //         diff.Differences.Add($"{Yellow}HASH INDEX DIFFERENCES{Reset}");
    //         diff.Differences.AddRange(section);
    //         diff.Differences.Add(string.Empty);
    //     }
    // }

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
    {
        // Canonical 32-hex-character representation
        return $"{key.A:X16}{key.B:X16}";
    }

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