using System.IO;

namespace DuplicateFileFinderLibTests.TestUtils;

public class IoUtil
{
    private readonly string _root;

    public IoUtil(string root)
    {
        _root = root;

        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // swallow cleanup issues (file locks etc.)
        }
    }
    
    public string CreateDir(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public  string CreateFile(string relative, byte[] content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }
}