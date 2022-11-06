using System.IO;

namespace DuplicateFileFinder.Models
{
    class DummyFileSystemObjectModel : FileSystemObjectModel
    {
        public DummyFileSystemObjectModel()
            : base(new DirectoryInfo("DummyFilySystemObjectInfo"))
        {
        }
    }
}
