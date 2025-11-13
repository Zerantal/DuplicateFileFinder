using DuplicateFileFinder.Gui.Models;
using DuplicateFileFinder.Gui.Util;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.ViewModels;

public class DuplicatesGridViewModel(Repo repo)
{
    // Guid DirId -> full path cache
    private readonly Dictionary<Guid, string> _dirPathCache = new();
    public BulkObservableCollection<DuplicateSetRow> Rows { get; } = new();
    public int TotalSets => Rows.Count;
    public long TotalFiles { get; private set; }
    
    public void LoadFromRepo()
    {
        // Only sets with count >= 2
        var rows = repo.HashIndex
            .Where(kv => kv.Value.Count >= 2)
            .Select(kv =>
            {
                var files = kv.Value.Select(id => repo.Files[id]).ToList();
                return new DuplicateSetRow(kv.Key, files, r => Path.Combine(GetFullDirPath(r.DirId), r.Name));
            });
        Reset(rows);
    }
    
    // Full replace (startup, scan switch)
    public void Reset(IEnumerable<DuplicateSetRow> rows)
    {
        Rows.AddRange(rows, true);
        TotalFiles = repo.Files.Count;
    }

    // Incremental from repo deltas
    public void Upsert(HashKey hash, IReadOnlyList<FileRecord> files)
    {
        var row = Rows.FirstOrDefault(r => r.Hash.Equals(hash));
        if (files.Count < 2)
        {
            if (row != null) Rows.Remove(row);
            return;
        }
        if (row == null) Rows.Add(new DuplicateSetRow(hash, files, _ => String.Empty));
        else row.Update(files);
    }
    
    private string GetFullDirPath(Guid dirId)
    {
        if (_dirPathCache.TryGetValue(dirId, out var cached))
            return cached;

        var stack = new Stack<string>();
        var currentId = dirId;

        while (true)
        {
            if (!repo.Dirs.TryGetValue(currentId, out var dir))
                throw new InvalidOperationException($"DirId {currentId} not found in repo.");

            stack.Push(dir.Name);

            if (dir.ParentId is null)
                break;

            currentId = dir.ParentId.Value;
        }

        var fullPath = Path.Combine(stack.ToArray());
        _dirPathCache[dirId] = fullPath;
        return fullPath;
    }
}