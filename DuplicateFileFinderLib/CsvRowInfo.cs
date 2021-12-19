using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuplicateFileFinderLib
{
    internal record CsvRowData
    {
        public bool IsFile { get; init; }
        public string Path { get; init; }
        public long Size { get; init; }
        public int FileCount { get; init; }
        public string Extension { get; init; }
        public string Checksum { get; init; }
        public int Group { get; init; }

    }
}
