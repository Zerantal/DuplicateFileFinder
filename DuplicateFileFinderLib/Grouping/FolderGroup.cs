using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLib.Grouping;

internal class FolderGroup(int folderGroup)
{
    private readonly List<FolderNode> _folders = [];

    public void AddFolder(FolderNode folder)
    {
        folder.Group = folderGroup;
        _folders.Add(folder);
    }
}