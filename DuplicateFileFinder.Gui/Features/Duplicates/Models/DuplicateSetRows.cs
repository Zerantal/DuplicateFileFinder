using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Models;

public sealed partial class DuplicateSetRow : ObservableObject
{
    private readonly List<FileItem> _items = new();

    private readonly List<(FileRecordV2 FileRecord, string Name, Func<string> pathResolver)> _files;

    [ObservableProperty] private bool _isSelected;

    public DuplicateSetRow(IEnumerable<(FileRecordV2 fileRecord, string name, Func<string> pathResolver)> files)
    {
        _files = files.ToList();
        RepresentativeName = _files.Count > 0 ? _files[0].Name : string.Empty;
        RebuildItems();
    }
    
    public int Count => _files.Count;
    public long TotalBytes => _files.Sum(f => f.FileRecord.Size);

    // Name/path of the first element of the set
    public string RepresentativeName { get; }

    public IReadOnlyList<FileItem> Items => _items;
    
    private void RebuildItems()
    {
        _items.AddRange(_files.Select(r =>
            new FileItem(
                r.FileRecord.FileId,
                r.Name,
                r.pathResolver(), 
                r.FileRecord.Size,
                new DateTimeOffset(r.FileRecord.ModifiedTicks, TimeSpan.Zero))));
    }
}