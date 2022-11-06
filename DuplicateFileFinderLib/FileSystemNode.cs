using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NLog.LayoutRenderers.Wrappers;

namespace DuplicateFileFinderLib
{
    public abstract class FileSystemNode
    {
        public string Checksum { get; protected set; } = string.Empty;
        public string Path { get; protected set; }
        public int Group { get; internal set; } = -2;
        public long Size { get; protected set; } // in bytes

        public ReadOnlyCollection<FolderNode> SubFolders => new(Children.Where(n => n is FolderNode).Cast<FolderNode>().ToArray());

        public ReadOnlyCollection<FileNode> Files =>
            new(Children.Where(n => n is FileNode).Cast<FileNode>().ToArray());

        protected readonly List<FileSystemNode> Children = new();

        protected FileSystemNode(string path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
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
}
