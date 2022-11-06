using System.IO;

namespace Deduplicator.Models;

internal class DummyFileSystemObjectModel : FileSystemObjectModel
{
    public DummyFileSystemObjectModel()
        : base(new DirectoryInfo("DummyFileSystemObjectInfo"))
    {
    }
}