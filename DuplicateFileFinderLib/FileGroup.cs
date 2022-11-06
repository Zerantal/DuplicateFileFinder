namespace DuplicateFileFinderLib;

public class FileGroup : GroupBase
{
    // ReSharper disable once CollectionNeverQueried.Local
    private readonly List<FileNode> _files = new();

    public FileGroup(FileNode file)
    {
        AddFile(file);
    }

    public IReadOnlyCollection<FileNode> Files => _files;

    public void AddFile(FileNode file)
    {
        file.Group = this;
        _files.Add(file);
    }

    public override int DuplicateCount => _files.Count;
}