using System;
using System.IO;
using System.Linq;

namespace DuplicateFileFinderLibTests.TestUtils;

public sealed class TempFsFixture : IDisposable
{
    public string Root { get; init; }

    public TempFsFixture(string root = "DFFTests_")
    {
        Root = Path.Combine(Path.GetTempPath(), root + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Dir(params string[] parts)
    {
        var p = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relPath, ReadOnlySpan<byte> content, DateTimeOffset? createdUtc = null)
    {
        var full = Path.Combine(Root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content.ToArray());
        if (createdUtc.HasValue)
            System.IO.File.SetCreationTimeUtc(full, createdUtc.Value.UtcDateTime);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        catch { /* ignore */ }
    }
}