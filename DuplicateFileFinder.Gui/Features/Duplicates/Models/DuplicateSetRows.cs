// Features/Controller/Models/DuplicateSetRows.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Models;

public sealed partial class DuplicateSetRow : ObservableObject
{
    // Note: items are loaded lazily when the row is selected.
    private readonly ObservableCollection<FileItem> _items = new();

    private readonly FileHandle _fileRepresentative;

    [ObservableProperty] private bool _isSelected;
    private readonly Func<FileHandle, string?>? _nameResolver;

    public DuplicateSetRow(
        HashGroupDescriptor descriptor,
        Func<FileHandle, string?> nameResolver)
    {
        Descriptor = descriptor;
        Hash = descriptor.Hash;
        _nameResolver = nameResolver;
        _fileRepresentative = descriptor.FirstFile;
        Count = descriptor.Count;
        TotalBytes = descriptor.SizeBytes * descriptor.Count;

        Items = new ReadOnlyObservableCollection<FileItem>(_items);
    }

    public HashKey Hash { get; }
    public HashGroupDescriptor Descriptor { get; }
    public ReadOnlyObservableCollection<FileItem> Items { get; }

    public int Count { get; private set; }
    public long TotalBytes { get; private set; }

    public string RepresentativeName
    {
        get
        {
            if (field is not null)
                return field;

            field = _nameResolver?.Invoke(_fileRepresentative);
            return field ?? string.Empty;
        }
    }

    public void SetItems(IEnumerable<FileItem> items)
    {
        _items.Clear();
        foreach (var i in items)
            _items.Add(i);
    }

    public bool TryRemoveItemByFileId(long fileId)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id == fileId)
            {
                _items.RemoveAt(i);
                Count = _items.Count;
                TotalBytes = _items.Sum(x => x.Size);
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(TotalBytes));
                return true;
            }

        return false;
    }
}
