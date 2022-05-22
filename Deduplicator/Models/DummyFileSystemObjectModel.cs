using System.IO;

namespace DuplicateFileFinder.Models;

internal class DummyFileSystemObjectModel : FileSystemObjectModel
{
    public DummyFileSystemObjectModel()
        : base(new DirectoryInfo("DummyFileSystemObjectInfo"))
    {
    }
}