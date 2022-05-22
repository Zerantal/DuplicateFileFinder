using System.Diagnostics.CodeAnalysis;

namespace DuplicateFileFinderLib;

[ExcludeFromCodeCoverage]
internal class FolderGroup : GroupBase
{
    // ReSharper disable once CollectionNeverQueried.Local
    private readonly List<FolderNode> _folders = new();

    public FolderGroup(FolderNode folder)
    {
        AddFolder(folder);
    }

    public void AddFolder(FolderNode folder)
    {
        folder.Group = this;
        _folders.Add(folder);
    }

    public override int DuplicateCount => _folders.Count;
}
