using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinder.Models;
using DuplicateFileFinder.Properties;

namespace DuplicateFileFinder
{
    public class DuplicateFiles 
    {
        static long FileGroupCounter = 0;

        private List<FileInfo> _duplicateFiles;
        private long _fileSize;
        private string _md5 = null;

        public DuplicateFiles(FileInfo file)
        {
            _fileSize = file.Length;
            _duplicateFiles = new List<FileInfo> (new FileInfo[] {file});
        }

        private string Md5
        {
            get
            {
                if (_md5 == null)
                    _md5 = GetMD5HashFromFile(_duplicateFiles.First().FullName);
                
                return _md5;
            }
        }

        public IReadOnlyList<FileInfo> Copies => _duplicateFiles;

        public long FileSize => _fileSize;

        public void AddDuplicate(FileInfo file)
        {
            if (FileSize != file.Length)
                throw new ArgumentException("File size mismatch");

            _duplicateFiles.Add(file);
        }

        private string GetMD5HashFromFile(string fileName)
        {
            using (var MD5 = System.Security.Cryptography.MD5.Create())
            {
                using (var stream = File.OpenRead(fileName))
                {
                    return BitConverter.ToString(MD5.ComputeHash(stream)).Replace("-", string.Empty);
                }
            }
        }

        public override bool Equals(object obj)
        {
            var item = obj as DuplicateFiles;

            if (item == null)
                return false;

            if (FileSize != item.FileSize)
                return false;

            return Md5 == item.Md5;
        }

        public override int GetHashCode()
        {
            return FileSize.GetHashCode();
        }

        private static long GetGroupId()
        {
            return Interlocked.Increment(ref FileGroupCounter);
        }

        public List<DuplicateFileModel> GetDuplicatesFileModels()
        {
            var groupId = GetGroupId();

            List<DuplicateFileModel> files = new List<DuplicateFileModel>();
            foreach (var file in _duplicateFiles)
                files.Add(new DuplicateFileModel(groupId, file));

            return files;
        }
    }
}
