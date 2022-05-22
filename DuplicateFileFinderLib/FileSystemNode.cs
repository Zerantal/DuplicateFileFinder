using System.Collections.ObjectModel;

namespace DuplicateFileFinderLib;

public abstract class FileSystemNode
{
    protected readonly List<FileSystemNode> Children = new();

    protected FileSystemNode(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Checksum { get; protected set; } = string.Empty;
    public string Path { get; protected set; }
    public GroupBase? Group { get; internal set; }
    public long Size { get; protected set; } // in bytes

    public ReadOnlyCollection<FolderNode> SubFolders =>
        new(Children.Where(n => n is FolderNode).Cast<FolderNode>().ToArray());

    public ReadOnlyCollection<FileNode> Files =>
        new(Children.Where(n => n is FileNode).Cast<FileNode>().ToArray());

    public ReadOnlyCollection<FileSystemNode> GetChildren()
    {
        return new ReadOnlyCollection<FileSystemNode>(Children);
    }

    public abstract string Name { get; }

    protected abstract void WriteCsvEntry(TextWriter writer);

    public void WriteCsvEntries(TextWriter writer)
    {
        WriteCsvEntry(writer);

        foreach (var f in Files)
            f.WriteCsvEntries(writer);

        foreach (var f in SubFolders)
            f.WriteCsvEntries(writer);
    }

    public virtual void AddFileSystemNode(FileSystemNode node)
    {
        Children.Add(node);
    }
}