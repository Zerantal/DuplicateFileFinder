﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuplicateFileFinderLib
{
    public class InvalidFormatException : Exception
    {
        public string File { get; } = string.Empty;

        public InvalidFormatException() : base() { }

        public InvalidFormatException(string errMsg) :base (errMsg) { }
        
        public InvalidFormatException(string errMsg, Exception e) : base(errMsg, e) { }

        public InvalidFormatException(string file, string errMsg) : base(errMsg)
        {
            File = file;
        }

        public InvalidFormatException(string file, string errMsg, Exception e) : base(errMsg, e)
        {
            File = file;
        }

    }
}
