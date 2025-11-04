// DuplicateFileFinderLib/Core/DuplicateFileFinder.cs

using DuplicateFileFinderLib.FileSystem;
using DuplicateFileFinderLib.Grouping;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Core;

public enum ImportMode
{
    Merge,
    Replace
}

public sealed class DuplicateFileFinder
{
    private readonly IChecksumPipeline _checksums;
    private readonly Dictionary<long, int> _fileSizes = new(); // filesize => count
    private readonly IFileEnumerator _fs;
    private readonly IGroupingService _grouping;
    private readonly IScanSerializer _serializer;

    private RootNode _root = new();

    internal DuplicateFileFinder(IFileEnumerator? fs = null,
        IChecksumPipeline? checksums = null,
        IGroupingService? grouping = null,
        IScanSerializer? serializer = null)
    {
        _fs = fs ?? new FileEnumerator();
        _checksums = checksums ?? new ChecksumPipeline();
        _grouping = grouping ?? new FileSystemGroupsAdapter();
        _serializer = serializer ?? new CsvScanSerializer();
    }

    public DuplicateFileFinder() : this(null)
    {
    }
    
    public IReadOnlyList<string> SearchPaths
        => _root.SubFolders.Select(f => PathUtils.NormalizePath(f.Path)).ToArray();

    public int TotalFilesScanned => _root.SubFolders.Sum(l => l.AggregateFileCount);
    public long DuplicateSpaceBytes => ComputeDuplicateSpaceBytes();
    public int DuplicateFilesWastedCount => ComputeDuplicateWastedFileCount();

    // ------------ Public scanning API ----------------

    public async Task ScanLocation(string location,
        IProgress<DuplicateFileFinderProgressReport>? progressIndicator = null,
        CancellationToken token = default)
    {
        location = PathUtils.NormalizePath(location);

    // Figure out topology case using current, unmodified _root
        var existingAncestor = _root.SubFolders.FirstOrDefault(r =>
            PathUtils.IsSamePath(r.Path, location) || PathUtils.IsAncestorOfPath(r.Path, location));

    var hasDescendants = _root.SubFolders.Any(r => PathUtils.IsAncestorOfPath(location, r.Path));

    // Prepare transactional working state (never touching _root yet)
    RootNode workRoot;
    FolderNode workScope;

        if (existingAncestor is not null)
        {
        // Case 1: clone only the affected subtree, scan into the clone
        workRoot = new RootNode();
        // keep other roots untouched in workRoot; they aren’t needed for this scope
        workScope = existingAncestor.DeepCloneSubtree();
        workRoot.AddFileSystemNode(workScope);
        }
    else if (hasDescendants)
    {
        // Case 2: promotion scenario – build a promoted root in a workspace
        // Start from a shallow copy of roots, then rehome via promoter
        workRoot = new RootNode();
        foreach (var r in _root.SubFolders)
            workRoot.AddFileSystemNode(r.DeepCloneSubtree()); // safe to clone; commit happens later

        workRoot = Tree.TreePromoter.PromoteAncestor(workRoot, location);
        workScope = workRoot.SubFolders.First(r => PathUtils.IsSamePath(r.Path, location));
    }
    else
        {
        // Case 3: independent root – create a fresh scope in the workspace
        workRoot = new RootNode();
        workScope = new FolderNode(location);
        workRoot.AddFileSystemNode(workScope);
        }

    // Build a temporary size map for hashing decisions
    var tempSizes = new Dictionary<long, int>();

    // Local enumerator that doesn’t touch _root
    void PopulateTemp(FolderNode folder, CancellationToken ct)
    {
        if (folder.SubFolders.Count > 0 || folder.Files.Count > 0) return;

        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingDirs  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in _fs.EnumerateChildren(folder.Path, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (e.IsDirectory)
            {
                if (existingDirs.Add(e.FullPath))
                    folder.AddFileSystemNode(new FolderNode(e.FullPath));
            }
            else
            {
                if (existingFiles.Add(e.FullPath))
                {
                    var fn = new FileNode(e.FullPath, e.Length);
                    folder.AddFileSystemNode(fn);
                    tempSizes[fn.Size] = tempSizes.TryGetValue(fn.Size, out var n) ? n + 1 : 1;
                }
            }
        }
    }

    // The transactional scan pipeline
    try
    {
        // enumerate into workScope only
        await workScope.TraverseFolders(
            down: async f =>
            {
                token.ThrowIfCancellationRequested();
                await Task.Run(() => PopulateTemp(f, token), token);
                progressIndicator?.Report(new DuplicateFileFinderProgressReport
                {
                    StatusMessage = $"Scanning {f.Path} ..."
                });
            },
            up: f =>
            {
                token.ThrowIfCancellationRequested();
                f.UpdateFolderStats();
                return Task.CompletedTask;
            });

        // checksums & grouping on workspace only
        progressIndicator?.Report(new DuplicateFileFinderProgressReport { StatusMessage = "Computing checksums..." });

        await _checksums.ComputeAsync(
            workScope,
            shouldHash: f => tempSizes.TryGetValue(f.Size, out var cnt) && cnt > 1 && f.ChecksumBytes == null,
            onProgress: p => progressIndicator?.Report(new DuplicateFileFinderProgressReport { PercentComplete = p }),
            ct: token);

        await _grouping.AssignAsync(workScope, token);

        // --- Commit: merge the successful workspace into _root ---
        if (existingAncestor is not null)
        {
            // Replace that subtree with the freshly scanned clone
            _root.ReplaceChildInRoot(workScope);
        }
        else if (hasDescendants)
        {
            // Entire promoted layout is the new truth: swap all promoted roots
            _root = workRoot;
        }
        else
        {
            // Independent new root: add it
            _root.ReplaceChildInRoot(workScope);
        }

        // Rebuild aggregates and size map from committed tree
        await _root.RecomputeSubtreeAggregatesAsync();

        progressIndicator?.Report(new DuplicateFileFinderProgressReport(isRunning: false)
        {
            StatusMessage = "Grouping complete",
            PercentComplete = 1.0
        });
    }
    catch
    {
        // Discard workspace on any failure: do NOT touch _root
        // Re-throw to preserve behavior
        throw;
    }
    }

    private void PopulateFolderChildrenFromDiskSafe(FolderNode folder, CancellationToken token)
    {
        if (folder.SubFolders.Count > 0 || folder.Files.Count > 0)
            return;

        var existingFiles = new HashSet<string>(folder.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var existingDirs = new HashSet<string>(folder.SubFolders.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var e in _fs.EnumerateChildren(folder.Path, token))
        {
            token.ThrowIfCancellationRequested();

            if (e.IsDirectory)
            {
                if (existingDirs.Add(e.FullPath))
                    folder.AddFileSystemNode(new FolderNode(e.FullPath));
            }
            else
            {
                if (existingFiles.Add(e.FullPath))
                {
                    var fn = new FileNode(e.FullPath, e.Length);
                    folder.AddFileSystemNode(fn);

                    // build size index on the fly
                    _fileSizes[fn.Size] = _fileSizes.TryGetValue(fn.Size, out var n) ? n + 1 : 1;
                }
            }
        }
    }

    private async Task AfterEnumerationAsync(
        FolderNode scope,
        IProgress<DuplicateFileFinderProgressReport>? progress,
        CancellationToken token)
    {
        progress?.Report(new DuplicateFileFinderProgressReport { StatusMessage = "Computing checksums..." });

        await _checksums.ComputeAsync(
            scope,
            f => _fileSizes.TryGetValue(f.Size, out var cnt) && cnt > 1 && f.ChecksumBytes == null,
            p => progress?.Report(new DuplicateFileFinderProgressReport { PercentComplete = p }),
            token);

        await _grouping.AssignAsync(scope, token);

        progress?.Report(new DuplicateFileFinderProgressReport(false)
        {
            StatusMessage = "Grouping complete",
            PercentComplete = 1.0
        });
    }

    // ------------ CSV I/O ---------------

    public void ClearAllScans()
    {
        _root = new RootNode();
        _fileSizes.Clear();
        _grouping.Reset();
    }

    public void ExportToCsv(TextWriter writer)
    {
        _serializer.Export(_root, writer);
    }

    public void ImportFromCsv(TextReader reader, ImportMode mode = ImportMode.Merge)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (mode == ImportMode.Replace) ClearAllScans();

        _serializer.ImportInto(_root, reader);

        // recompute aggregates
        foreach (var top in _root.SubFolders)
            top.TraverseFolders(
                null,
                f =>
                {
                    f.UpdateFolderStats();
                    return Task.CompletedTask;
                }
            ).Wait();

        // rebuild _fileSizes
        _fileSizes.Clear();
        foreach (var file in EnumerateAllFiles())
            _fileSizes[file.Size] = _fileSizes.TryGetValue(file.Size, out var n) ? n + 1 : 1;
    }

    // ------------ Queries ----------------

    public async Task<IReadOnlyList<DuplicateFileRow>> GetDuplicateFileRowsAsync()
    {
        var results = new List<DuplicateFileRow>();
        var groups = new Dictionary<int, List<FileNode>>();

        foreach (var top in _root.SubFolders)
            await top.TraverseFolders(folder =>
            {
                foreach (var f in folder.Files)
                {
                    if (f.Group < 0) continue;
                    if (!groups.TryGetValue(f.Group, out var list))
                        groups[f.Group] = list = new List<FileNode>();
                    list.Add(f);
                }

                return Task.CompletedTask;
            });

        foreach (var kv in groups.Where(kv => kv.Value.Count > 1))
        foreach (var f in kv.Value)
        {
            var folderPath = Path.GetDirectoryName(f.Path) ?? string.Empty;
            var ext = Path.GetExtension(f.Path);

            DateTime creationUtc;
            try
            {
                creationUtc = File.GetCreationTimeUtc(f.Path);
            }
            catch
            {
                creationUtc = DateTime.MinValue;
            }

            results.Add(new DuplicateFileRow
            {
                Path = f.Path,
                Size = f.Size,
                CreationTimeUtc = creationUtc,
                Folder = folderPath,
                Extension = ext,
                Checksum = f.ChecksumHex,
                Group = f.Group
            });
        }

        return results;
    }

    private long ComputeDuplicateSpaceBytes()
    {
        var groups = new Dictionary<int, (long total, long rep, int count)>();
        foreach (var f in EnumerateAllFiles())
        {
            if (f.Group < 0) continue;
            var acc = groups.GetValueOrDefault(f.Group);
            acc.total += f.Size;
            acc.count++;
            if (acc.rep == 0) acc.rep = f.Size;
            groups[f.Group] = acc;
        }

        long wasted = 0;
        foreach (var a in groups.Values)
            if (a.count > 1)
                wasted += a.total - a.rep;
        return wasted;
    }

    private int ComputeDuplicateWastedFileCount()
    {
        var counts = new Dictionary<int, int>();
        foreach (var f in EnumerateAllFiles())
        {
            if (f.Group < 0) continue;
            counts[f.Group] = counts.TryGetValue(f.Group, out var n) ? n + 1 : 1;
        }

        var wasted = 0;
        foreach (var c in counts.Values)
            if (c > 1)
                wasted += c - 1;
        return wasted;
    }

    private IEnumerable<FileNode> EnumerateAllFiles()
    {
        foreach (var loc in _root.SubFolders)
        {
            var buf = new List<FileNode>();
            loc.TraverseFolders(f =>
            {
                buf.AddRange(f.Files);
                return Task.CompletedTask;
            }).Wait();
            foreach (var f in buf) yield return f;
        }
    }
}