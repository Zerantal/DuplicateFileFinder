using System;
using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLibTests.TestUtils;

public sealed class FileNodeBuilder
{
    private string _path = "/tmp/file";
    private long _size;
    private DateTimeOffset _created;
    private byte[]? _checksum;
    private int? _group;

    public FileNodeBuilder Path(string p) { _path = p; return this; }
    public FileNodeBuilder Size(long s) { _size = s; return this; }
    public FileNodeBuilder Created(DateTimeOffset t) { _created = t; return this; }
    public FileNodeBuilder Checksum(byte[] h) { _checksum = h; return this; }

    public FileNodeBuilder Group(int g)
    {
        _group = g;
        return this;
    }

    public FileNode Build()
    {
        var n = new FileNode(_path, _size, _created);
        if (_checksum != null) n.ChecksumBytes = _checksum;
        if (_group != null) n.Group = _group.Value;
        return n;
    }
}

public sealed class FolderNodeBuilder(string path, DateTimeOffset created = default)
{
    private readonly FolderNode _folder = new(path, created);

    public FolderNodeBuilder File(FileNode f) { _folder.AddFileSystemNode(f); return this; }
    public FolderNodeBuilder Folder(FolderNode f) { _folder.AddFileSystemNode(f); return this; }

    public FolderNode Build() => _folder;
}