using System.IO.MemoryMappedFiles;

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage;

/// <summary>
/// Loads MemoryPack payloads using a memory-mapped file to provide a single contiguous ReadOnlySpan&lt;byte&gt;
/// to MemoryPack, avoiding the stream deserializer which rents buffers from
/// ArrayPool.Shared
/// </summary>
internal static class MemoryPackFile
{
    // MemoryPack's span-based APIs are int-length; enforce that up-front.
    private const long MaxMappedLengthBytes = int.MaxValue;
    private const string MappedLoadErrorPrefix = "MemoryPack mapped load only supports files up to";

    internal static T? LoadMapped<T>(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ct.ThrowIfCancellationRequested();

        var length = ValidateAndGetLength(path);
        if (length == 0)
            return default;

        ct.ThrowIfCancellationRequested();

        using var mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, access: MemoryMappedFileAccess.Read);

        using var accessor = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

        ct.ThrowIfCancellationRequested();

        // NOTE: cannot be cooperatively cancelled once inside Deserialize.
        return DeserializeFromAccessor<T>(accessor, length);
    }

    internal static bool TryLoadMapped<T>(string path, out T? value, CancellationToken ct = default)
    {
        try
        {
            value = LoadMapped<T>(path, ct);
            return true;
        }
        catch (Exception)
        {
            value = default;
            return false;
        }
    }

    private static long ValidateAndGetLength(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found.", path);

        var length = fileInfo.Length;

        if (length > MaxMappedLengthBytes)
        {
            throw new InvalidDataException(
                $"{MappedLoadErrorPrefix} {MaxMappedLengthBytes} bytes. File was {length} bytes: {path}");
        }

        return length;
    }

    private static unsafe T? DeserializeFromAccessor<T>(MemoryMappedViewAccessor accessor, long length)
    {
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            var viewPtr = ptr + accessor.PointerOffset;
            var span = new ReadOnlySpan<byte>(viewPtr, checked((int)length));
            return MemoryPackSerializer.Deserialize<T>(span);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    internal static void SaveToFile<T>(string path, in T value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1 << 20,
            options: FileOptions.SequentialScan);

        using var writer = new StreamBufferWriter(fs);

        MemoryPackSerializer.Serialize(writer, value);

        writer.Flush();
        fs.Flush(flushToDisk: false);
    }
}
