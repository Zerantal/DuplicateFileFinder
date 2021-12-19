using System;
using System.Collections.ObjectModel;
using System.IO;
using DuplicateFileFinder.Models;

namespace DuplicateFileFinder.ViewModels
{
    public class FileSystemViewModel
    {
        private readonly ObservableCollection<FileSystemObjectModel> _rootFileSystemObjects;

        public ObservableCollection<FileSystemObjectModel> RootFileSystemObjects => _rootFileSystemObjects;

        public FileSystemViewModel()
        {
            _rootFileSystemObjects = new ObservableCollection<FileSystemObjectModel>();
        }

        public EventHandler BeforeExplore;

        public EventHandler AfterExplore;

        public void AddFileSystemObject(FileSystemInfo info)
        {
            var fileSystemObject = new FileSystemObjectModel(info);
            fileSystemObject.BeforeExplore += BeforeExplore;
            fileSystemObject.AfterExplore += AfterExplore;
            
            _rootFileSystemObjects.Add(fileSystemObject);
        }

    }
}
