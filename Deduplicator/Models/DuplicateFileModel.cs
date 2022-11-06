using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuplicateFileFinder.Models
{
    public class DuplicateFileModel : BaseObjectModel
    {
        private FileInfo _file;

        public DuplicateFileModel(long groupId, FileInfo file)
        {
            FileGroup = groupId;
            _file = file;
        }

        public string FileName => _file.Name;

        public long FileSize => _file.Length;

        public string CreationDate => _file.CreationTime.ToLongDateString();

        public string Folder => _file.DirectoryName;

        public long FileGroup { get; }
    }
}
