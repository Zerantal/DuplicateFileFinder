using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.Models;

public sealed partial class DuplicateSetRow : ObservableObject
{
    private readonly List<FileItem> _items = new();
    private readonly Func<FileRecord, string> _pathResolver;

    private List<FileRecord> _files;

    [ObservableProperty] private bool _isSelected;

    public DuplicateSetRow(HashKey hash, IEnumerable<FileRecord> files, Func<FileRecord, string> pathResolver)
    {
        Hash = hash;
        _files = files.ToList();
        _pathResolver = pathResolver;
        RebuildItems();
    }

    public HashKey Hash { get; }
    public int Count => _files.Count;
    public long TotalBytes => _files.Sum(f => f.Size);

    // Name/path of the first element of the set
    public string RepresentativeName => _files.Count > 0 ? _files[0].Name : string.Empty;
    public string RepresentativePath => _files.Count > 0 ? _pathResolver(_files[0]) : string.Empty;

    public IReadOnlyList<FileItem> Items => _items;

    public void Update(IEnumerable<FileRecord> files)
    {
        _files = files.ToList();
        RebuildItems();
    }

    private void RebuildItems()
    {
        _items.AddRange(_files.Select(r =>
            new FileItem(
                r.FileId,
                r.Name,
                _pathResolver(r), r.Size,
                r.Modified!.Value)));
    }
}