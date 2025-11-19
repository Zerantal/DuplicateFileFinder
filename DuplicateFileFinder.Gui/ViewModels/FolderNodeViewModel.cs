// ViewModels/FolderNodeViewModel.cs

using System.Collections.ObjectModel;

namespace DuplicateFileFinder.Gui.ViewModels;

public sealed class FolderNodeViewModel
{
    public FolderNodeViewModel(Guid dirId, string name, string fullPath)
    {
        DirId = dirId;
        Name = name;
        FullPath = fullPath;
    }

    public Guid DirId { get; }
    public string Name { get; }
    public string FullPath { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public override string ToString()
    {
        return FullPath;
    }
}