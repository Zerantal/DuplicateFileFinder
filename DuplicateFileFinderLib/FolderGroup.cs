namespace DuplicateFileFinderLib
{
    internal class FolderGroup
    {
        private readonly int _folderGroup;

        // ReSharper disable once CollectionNeverQueried.Local
        private readonly List<FolderNode> _folders = new();

        public FolderGroup(int folderGroup)
        {
            _folderGroup = folderGroup;
        }

        public void AddFolder(FolderNode folder)
        {
            folder.Group = _folderGroup;
            _folders.Add(folder);
        }
    }
}
