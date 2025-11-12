using System.Collections.ObjectModel;

namespace DuplicateFileFinderLib.Tree;

public abstract class FileSystemNode(
    string path,
    DateTimeOffset creationTimeUtc = default,
    DateTimeOffset modifiedTimeUtc = default )
{
    protected readonly List<FileSystemNode> Children = [];
    public byte[]? ChecksumBytes { get; protected internal set; }

    public string ChecksumHex
    {
        get => ChecksumBytes is { Length: > 0 } b ? Convert.ToHexString(b) : string.Empty;
        protected set => ChecksumBytes = string.IsNullOrEmpty(value) ? null : Convert.FromHexString(value);
    }

    public string Path { get; protected set; } = path ?? throw new ArgumentNullException(nameof(path));
    public int Group { get; internal set; } = -2;
    public long Size { get; protected internal set; } // in bytes

    public DateTimeOffset CreationTimeUtc { get; protected set; } = creationTimeUtc;
    
    public DateTimeOffset ModifiedTimeUtc { get; protected set; } = modifiedTimeUtc; 

    public ReadOnlyCollection<FolderNode> SubFolders
    {
        get => new([.. Children.OfType<FolderNode>()]);
        set => throw new NotImplementedException();
    }

    public ReadOnlyCollection<FileNode> Files
    {
        get => new([.. Children.OfType<FileNode>()]);
        set => throw new NotImplementedException();
    }

    internal bool RemoveChild(FileSystemNode node)
    {
        return Children.Remove(node);
    }

    protected void CopyCommonFieldsTo(FileSystemNode target)
    {
        // Path is fixed by constructor
        target.Size = Size;
        target.Group = Group;

        if (ChecksumBytes is { Length: > 0 })
            target.ChecksumBytes = (byte[])ChecksumBytes.Clone();
        else
            target.ChecksumBytes = null;
    }

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