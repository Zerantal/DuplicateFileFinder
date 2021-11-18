using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DuplicateFileFinder.Models;
using DuplicateFileFinder.Util;

namespace DuplicateFileFinder.ViewModels
{
    public class DuplicateFilesViewModel : BaseObjectModel
    {
        public ObservableCollection<string> SearchPaths { get; }
        public ObservableCollection<DuplicateFileModel> DuplicateFiles { get; }

        public System.Threading.SynchronizationContext _syncContext;

        public DuplicateFilesViewModel()
        {
            _syncContext = System.Threading.SynchronizationContext.Current;

            SearchPaths = new ObservableCollection<string>();
            DuplicateFiles = new ObservableCollection<DuplicateFileModel>();
            ReadyToScan = false;
            IsScanning = false;
            SearchPaths.CollectionChanged += (sender, args) =>
            {
                var paths = sender as ObservableCollection<string>;
                if ((paths.Count > 0) & !IsScanning)
                    ReadyToScan = true;
            };
        }

        public async Task StartScan()
        {
            ReadyToScan = false;
            IsScanning = true;

            HashSet<DuplicateFiles> duplicates = new HashSet<DuplicateFiles>();

            Operation = "Searching for duplicates...";
            // Scan directories
            var progressSlice = 100.0 / SearchPaths.Count;
            HighPrecisionProgress = 0;
            FilesScanned = 0;
            DuplicatesFound = 0;
            SpaceTaken = 0;
            foreach (var dir in SearchPaths.Select(x => new DirectoryInfo(x)))
            {
                 await SearchDirectory(dir, duplicates, progressSlice);
            }

            //Operation = "Collating duplicate file list...";
            //progressSlice = 100.0 / duplicates.Count;
            //HighPrecisionProgress = 0;
            //foreach (var dups in duplicates)
            //{
            //    if (dups.Copies.Count > 1)
            //    {
            //        foreach (var f in dups.GetDuplicatesFileModels())
            //            _syncContext.Post(new System.Threading.SendOrPostCallback((o) => DuplicateFiles.Add(f)), null);
            //    }

            //    HighPrecisionProgress += progressSlice;
            //}

            Operation = "Finished Scanning.";
            ReadyToScan = true;
            IsScanning = false;

        }

        private async Task SearchDirectory(DirectoryInfo dir, HashSet<DuplicateFiles> duplicates, double progressSlice)
        {
            int numFolderObjects = dir.GetFiles().Length + dir.GetDirectories().Length;

            if (numFolderObjects == 0)
            {
                HighPrecisionProgress += progressSlice;
                return;
            }

            var newProgressSlice = progressSlice / (numFolderObjects);

            foreach (FileInfo file in dir.GetFiles())
            {
                DuplicateFiles newRecord = new DuplicateFiles(file);
                if (duplicates.TryGetValue(newRecord, out var existingRecord))
                {
                    existingRecord.AddDuplicate(file);
                    DuplicatesFound++;
                    SpaceTaken += file.Length;
                }
                else
                {
                    duplicates.Add(newRecord);
                }

                HighPrecisionProgress += newProgressSlice;
                FilesScanned++;
            }

            foreach (var subdir in dir.GetDirectories())
            {
                _ = SearchDirectory(subdir, duplicates, newProgressSlice);
            }

        }

        public void StopScan()
        {
            IsScanning = false;
            ReadyToScan = true;
            //throw new NotImplementedException();
        }

        private bool _isScanning;
        private bool _readyToScan;
        private int _progress;
        private string _operation;
        private int _filesScanned;
        private int _duplicatesFound;
        private long _spaceTaken;
        private double _highPrecisionProgress;

        public bool IsScanning
        {
            get => _isScanning;
            set
            {
                if (value == _isScanning) return;
                _isScanning = value;
                OnPropertyChanged(nameof(IsScanning));
            }
        }

        public bool ReadyToScan
        {
            get => _readyToScan;
            set
            {
                if (value == _readyToScan) return;
                _readyToScan = value;
                OnPropertyChanged(nameof(ReadyToScan));
            }
        }

        private double HighPrecisionProgress
        {
            get => _highPrecisionProgress;
            set
            {
                _highPrecisionProgress = value;
                Progress = (int) value;
            }
        }

        public int Progress
        {
            get => _progress;
            set
            {
                if (value == _progress) return;
                _progress = value;
                OnPropertyChanged(nameof(Progress));
            }
        }

        public string Operation
        {
            get => _operation;
            set
            {
                if (value == _operation) return;
                _operation = value;
                OnPropertyChanged(nameof(Operation));
            }
        }

        public int FilesScanned
        {
            get => _filesScanned;
            set
            {
                if (value == _filesScanned) return;
                _filesScanned = value;
                OnPropertyChanged(nameof(FilesScanned));
            }
        }

        public int DuplicatesFound
        {
            get => _duplicatesFound;
            set
            {
                if (value == _duplicatesFound) return;
                _duplicatesFound = value;
                OnPropertyChanged(nameof(DuplicatesFound));
            }
        }

        public Int64 SpaceTaken
        {
            get => _spaceTaken;
            set
            {
                if (value == _spaceTaken) return;
                _spaceTaken = value;
                OnPropertyChanged(nameof(SpaceTaken));
            }
        }
    }
}
