using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using DuplicateFileFinder.Enums;
using DuplicateFileFinder.Util;

namespace DuplicateFileFinder.Models
{
    public class DuplicateFileTreeNodeModel : BaseObjectModel
    {
        public ObservableCollection<DuplicateFileTreeNodeModel> Children { get; }

        private ImageSource _nodeIcon;

        public ImageSource NodeIcon
        {
            get => _nodeIcon;
            set
            {
                if (value == _nodeIcon) return;
                _nodeIcon = value;
                OnPropertyChanged(nameof(NodeIcon));
            }
        }

        public DuplicateFileTreeNodeModel()
        {
            Children = new ObservableCollection<DuplicateFileTreeNodeModel>();
        }

        public DuplicateFileTreeNodeModel(FileSystemInfo dir) : this()
        {
            Debug.Assert(dir is DirectoryInfo);

            NodeIcon = FolderManager.GetImageSource(dir.FullName, ItemState.Close);
        }

        public DuplicateFileTreeNodeModel(FileSystemInfo filePath, List<DuplicateFiles> duplicates) : this()
        {
            Debug.Assert(filePath is FileInfo);

            NodeIcon = FileManager.GetImageSource(filePath.FullName);
        }
    }
}
