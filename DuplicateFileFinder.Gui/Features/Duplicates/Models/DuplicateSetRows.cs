// Features/Controller/Models/DuplicateSetRows.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Models;

public sealed partial class DuplicateSetRow : ObservableObject
{
    private readonly ObservableCollection<FileItem> _items = new();
    public ReadOnlyObservableCollection<FileItem> Items { get; }

    private readonly List<(FileRecordV2 FileRecord, string Name, Func<string> pathResolver)> _files;

    [ObservableProperty] private bool _isSelected;

    public DuplicateSetRow(IEnumerable<(FileRecordV2 fileRecord, string name, Func<string> pathResolver)> files)
    {
        _files = files.ToList();
        RepresentativeName = _files.Count > 0 ? _files[0].Name : string.Empty;

        Items = new ReadOnlyObservableCollection<FileItem>(_items);

        RebuildItems();
    }

    public int Count => _items.Count;
    public long TotalBytes => _items.Sum(i => i.Size);

    // Name/path of the first element of the set
    public string RepresentativeName { get; }

    public void RebuildItems()
    {
        _items.Clear();

        foreach (var r in _files)
        {
            _items.Add(new FileItem(
                r.FileRecord.FileId,
                r.Name,
                r.pathResolver(),
                r.FileRecord.Size,
                new DateTimeOffset(r.FileRecord.ModifiedTicks, TimeSpan.Zero)));
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalBytes));
    }

    public bool TryRemoveItemByFileId(long fileId)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Id == fileId)
            {
                _items.RemoveAt(i);
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(TotalBytes));
                return true;
            }
        }
        return false;
    }
}
