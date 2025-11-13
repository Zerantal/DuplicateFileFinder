using System.Collections.ObjectModel;
using System.ComponentModel;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.Models;

public sealed class DuplicateSetRow : INotifyPropertyChanged
{
    private readonly Func<FileRecord, string> _pathResolver;
    public string HashHex => $"{Hash.A:x16}{Hash.B:x16}";

    public HashKey Hash { get; }
    public int Count => _files.Count;
    public long TotalBytes => _files.Sum(f => f.Size);
    
    // Name/path of the first element of the set
    public string RepresentativeName => _files.Count > 0 ? _files[0].Name : string.Empty;
    public string RepresentativePath => _files.Count > 0 ? _pathResolver(_files[0]) : string.Empty;

    public IReadOnlyList<FileItem> Items => _items;

    private List<FileRecord> _files;
    private readonly List<FileItem> _items = new();

    public DuplicateSetRow(HashKey hash, IEnumerable<FileRecord> files, Func<FileRecord, string> pathResolver)
    {
        Hash = hash;
        _files = files.ToList();
        _pathResolver = pathResolver;
        RebuildItems();
    }
    
    public void Update(IEnumerable<FileRecord> files)
    {
        _files = files.ToList();
        RebuildItems();
        Raise(nameof(Count));
        Raise(nameof(TotalBytes));
        Raise(nameof(Items));
        Raise(nameof(RepresentativeName));
        Raise(nameof(RepresentativePath));
        Raise(nameof(Items));
    }

    private void RebuildItems()
    {
        _items.AddRange(_files.Select(r => 
            new FileItem(
                r.Id, 
                r.Name, 
                _pathResolver(r), r.Size, 
                r.Modified)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}