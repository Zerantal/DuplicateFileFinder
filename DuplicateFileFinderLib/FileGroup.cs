namespace DuplicateFileFinderLib
{
    public class FileGroup
    {
        private readonly int _fileGroup;

        // ReSharper disable once CollectionNeverQueried.Local
        private readonly List<FileNode> _files = new();

        public FileGroup(int fileGroup)
        {
            _fileGroup = fileGroup;
        }

        public void AddFile(FileNode file)
        {
            file.Group = _fileGroup;
            _files.Add(file);
        }
    }
}
