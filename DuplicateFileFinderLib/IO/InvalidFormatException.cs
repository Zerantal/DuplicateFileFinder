namespace DuplicateFileFinderLib.IO;

public class InvalidFormatException : Exception
{
    public InvalidFormatException()
    {
    }

    public InvalidFormatException(string errMsg) : base(errMsg)
    {
    }

    public InvalidFormatException(string errMsg, Exception e) : base(errMsg, e)
    {
    }

    public InvalidFormatException(string file, string errMsg) : base(errMsg)
    {
        File = file;
    }

    public InvalidFormatException(string file, string errMsg, Exception e) : base(errMsg, e)
    {
        File = file;
    }

    public string File { get; } = string.Empty;
}