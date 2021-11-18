using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using DuplicateFileFinder.Enums;
using DuplicateFileFinder.Util;

namespace DuplicateFileFinder.Models
{
    public class FileSystemObjectModel : BaseObjectModel
    {
        public ObservableCollection<FileSystemObjectModel> Children { get; set; }
        

        public FileSystemObjectModel()
        {
            Children = new ObservableCollection<FileSystemObjectModel>();
        }

        public FileSystemObjectModel(FileSystemInfo info)
        : this()
        {
            if (this is DummyFileSystemObjectModel)
            {
                return;
            }

            Children = new ObservableCollection<FileSystemObjectModel>();
            FileSystemInfo = info;

            if (info is DirectoryInfo)
            {
                FileIcon = FolderManager.GetImageSource(info.FullName, ItemState.Close);
                AddDummy();
            }
            else if (info is FileInfo)
            {
                FileIcon = FileManager.GetImageSource(info.FullName);
            }

        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (value == _isSelected) return;
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        private bool _isExpanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (value == _isExpanded) return;
                if (FileSystemInfo is DirectoryInfo)
                {
                    RaiseBeforeExpand();
                    _isExpanded = value;
                    if (_isExpanded)
                    {
                        FileIcon = FolderManager.GetImageSource(FileSystemInfo.FullName, ItemState.Open);
                        if (HasDummy())
                        {
                            RaiseBeforeExplore();
                            RemoveDummy();
                            ExploreDirectories();
                            ExploreFiles();
                            RaiseAfterExplore();
                        }
                    }
                    else
                    {
                        FileIcon = FolderManager.GetImageSource(FileSystemInfo.FullName, ItemState.Close);
                    }

                    RaiseAfterExpand();
                }

                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        private DriveInfo Drive { get; set; }


        private void ExploreFiles()
        {
            if (Drive?.IsReady == false)
                return;

            if (FileSystemInfo is DirectoryInfo info)
            {
                var files = info.GetFiles();
                foreach (var file in files.OrderBy(d => d.Name))
                {
                    if ((file.Attributes & FileAttributes.System) != FileAttributes.System &&
                        (file.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                    {
                        Children.Add(new FileSystemObjectModel(file));
                    }
                }
            }
        }

        private void ExploreDirectories()
        {
            if (Drive?.IsReady == false)
                return;

            if (FileSystemInfo is DirectoryInfo info)
            {
                var directories = info.GetDirectories();
                foreach (var directory in directories.OrderBy(d => d.Name))
                {
                    if ((directory.Attributes & FileAttributes.System) != FileAttributes.System &&
                        (directory.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                    {
                        var fileSystemObject = new FileSystemObjectModel(directory);
                        fileSystemObject.BeforeExplore += FileSystemObject_BeforeExplore;
                        fileSystemObject.AfterExplore += FileSystemObject_AfterExplore;
                        Children.Add(fileSystemObject);
                    }
                }
            }
        }

        private void FileSystemObject_AfterExplore(object sender, EventArgs e)
        {
            RaiseAfterExplore();
        }

        private void FileSystemObject_BeforeExplore(object sender, EventArgs e)
        {
            RaiseBeforeExplore();
        }


        private FileSystemInfo _fileSystemInfo;

        public FileSystemInfo FileSystemInfo
        {
            get => _fileSystemInfo;
            set
            {
                if (value == _fileSystemInfo) return;
                _fileSystemInfo = value;
                OnPropertyChanged(nameof(FileSystemInfo));
            }
        }



        private ImageSource _fileIcon;

        public ImageSource FileIcon
        {
            get => _fileIcon;
            set
            {
                if (value == _fileIcon) return;
                _fileIcon = value;
                OnPropertyChanged(nameof(FileSystemInfo));
            }
        }      
        
        private void AddDummy()
        {
            Children.Add(new DummyFileSystemObjectModel());
        }

        private bool HasDummy()
        {
            return GetDummy() != null;
        }

        private DummyFileSystemObjectModel GetDummy()
        {
            return Children.OfType<DummyFileSystemObjectModel>().FirstOrDefault();
        }

        private void RemoveDummy()
        {
            Children.Remove(GetDummy());
        }

        public event EventHandler BeforeExpand;
        public event EventHandler AfterExpand;
        public event EventHandler BeforeExplore;
        public event EventHandler AfterExplore;

        private void RaiseBeforeExpand()
        {
            BeforeExpand?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseAfterExpand()
        {
            AfterExpand?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseBeforeExplore()
        {
            BeforeExplore?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseAfterExplore()
        {
            AfterExplore?.Invoke(this, EventArgs.Empty);
        }

    }
}
