// ViewModels/FolderNodeViewModel.cs

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicateFileFinder.Gui.ViewModels;

public sealed class FolderNodeViewModel : ObservableObject
{
    public FolderNodeViewModel(Guid dirId, string name, string fullPath)
    {
        DirId = dirId;
        _name = name;
        _fullPath = fullPath;
    }

    public Guid DirId { get; }

    private string _name;
    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    private string _fullPath;
    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (value == _fullPath) return;
            _fullPath = value;
            OnPropertyChanged();
        }
    }

    private bool _showFullPath;

    public bool ShowFullPath
    {
        get => _showFullPath;
        set
        {
            if (SetProperty(ref _showFullPath, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public FolderNodeViewModel? Parent { get; set; }
    
    public string DisplayName => ShowFullPath ? FullPath : Name;
    
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public override string ToString()
    {
        return FullPath;
    }
}