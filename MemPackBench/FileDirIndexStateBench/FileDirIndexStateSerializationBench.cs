using BenchmarkDotNet.Attributes;
using MemoryPack;

namespace MemPackBench.FileDirIndexStateBench;

// BenchmarkDotNet benchmark comparing MemoryPack serialization/deserialization of:
//
// V1: Dictionary<long, DirHandle/FileHandle>
// V2: KeyValuePair<long, DirHandle/FileHandle>[]
//
// Includes pipelines:
//  - V1 -> V2 -> disk (convert + serialize V2 + write file)
//  - disk -> V2 -> V1 (read file + deserialize V2 + convert)
//
// Notes:
// - “disk” timing will mostly measure OS page cache unless you flush/drop caches.
// - 1,000,000 files + 250,000 dirs is large. Ensure you run x64 Release, plenty of RAM.
// - Your IsValid properties look inverted (Index < 0). Left as-provided.
[MemoryDiagnoser]
[SimpleJob]
public class FileDirIndexStateSerializationBench
{
    static FileDirIndexStateSerializationBench()
    {
        // Ensure MemoryPack codegen is referenced by touching types (helps with trimming scenarios).
        _ = typeof(FileDirIndexStateV1);
        _ = typeof(FileDirIndexStateV2);
    }
    
    private const int FileCount = 3_000_000;
    private const int DirCount = 600_000;

    private FileDirIndexStateV1 _v1 = null!;
    private FileDirIndexStateV2 _v2 = null!;

    private byte[] _v1Bytes = null!;
    private byte[] _v2Bytes = null!;
    private string _tempFilePath = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Build big datasets once
        _v1 = BuildV1(FileCount, DirCount, scanRootCount: 16);

        _v2 = Conversions.ToV2(_v1);

        _v1Bytes = MemoryPackSerializer.Serialize(_v1);
        _v2Bytes = MemoryPackSerializer.Serialize(_v2);

        var tempDir = Path.Combine(Path.GetTempPath(), "dff-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempFilePath = Path.Combine(tempDir, "filedirindexstate_v2.mpk");

        // Ensure disk benchmarks start from a file that exists.
        File.WriteAllBytes(_tempFilePath, _v2Bytes);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            var dir = Path.GetDirectoryName(_tempFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* ignore */ }
    }

    // -----------------------
    // Pure serialization
    // -----------------------

    [Benchmark(Description = "Serialize V1 (Dictionary) -> bytes")]
    public byte[] Serialize_V1()
        => MemoryPackSerializer.Serialize(_v1);

    [Benchmark(Description = "Deserialize V1 (Dictionary) <- bytes")]
    public FileDirIndexStateV1 Deserialize_V1()
        => MemoryPackSerializer.Deserialize<FileDirIndexStateV1>(_v1Bytes)!;

    [Benchmark(Description = "Serialize V2 (KVP[]) -> bytes")]
    public byte[] Serialize_V2()
        => MemoryPackSerializer.Serialize(_v2);

    [Benchmark(Description = "Deserialize V2 (KVP[]) <- bytes")]
    public FileDirIndexStateV2 Deserialize_V2()
        => MemoryPackSerializer.Deserialize<FileDirIndexStateV2>(_v2Bytes)!;

    // -----------------------
    // Conversion costs
    // -----------------------

    [Benchmark(Description = "Convert V1 -> V2")]
    public FileDirIndexStateV2 Convert_V1_To_V2()
        => Conversions.ToV2(_v1);

    [Benchmark(Description = "Convert V2 -> V1")]
    public FileDirIndexStateV1 Convert_V2_To_V1()
        => Conversions.ToV1(_v2);

    // -----------------------
    // Pipelines requested
    // -----------------------

    [Benchmark(Description = "V1 -> V2 -> disk (convert + serialize V2 + write)")]
    public long Pipeline_V1_To_V2_To_Disk()
    {
        var v2 = Conversions.ToV2(_v1);
        var bytes = MemoryPackSerializer.Serialize(v2);

        // Overwrite file each run.
        using var fs = new FileStream(
            _tempFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            options: FileOptions.SequentialScan);

        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: false); // flush to OS, not necessarily to platter/SSD

        return bytes.Length;
    }

    [Benchmark(Description = "disk -> V2 -> V1 (read + deserialize V2 + convert)")]
    public int Pipeline_Disk_To_V2_To_V1()
    {
        byte[] bytes;

        using (var fs = new FileStream(
                   _tempFilePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 1024 * 1024,
                   options: FileOptions.SequentialScan))
        {
            bytes = new byte[fs.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }
        }

        var v2 = MemoryPackSerializer.Deserialize<FileDirIndexStateV2>(bytes)!;
        var v1 = Conversions.ToV1(v2);

        // Return something to keep the work “used”.
        return v1.FilesById.Count + v1.DirsById.Count;
    }

    // -----------------------
    // Data generator
    // -----------------------

    private static FileDirIndexStateV1 BuildV1(int fileCount, int dirCount, int scanRootCount)
    {
        // Use deterministic pseudo-random distribution without Random() overhead:
        // scanRootId cycles, index increments.
        var dirs = new Dictionary<long, DirHandle>(capacity: dirCount);
        var files = new Dictionary<long, FileHandle>(capacity: fileCount);

        // IDs monotonic to keep dictionary cheap (still hash-based, but stable).
        long dirId = 1;
        for (int i = 0; i < dirCount; i++, dirId++)
        {
            var scanRootId = (i % scanRootCount) + 1;
            dirs[dirId] = new DirHandle(scanRootId, i);
        }

        long fileId = 1;
        for (int i = 0; i < fileCount; i++, fileId++)
        {
            var scanRootId = (i % scanRootCount) + 1;
            files[fileId] = new FileHandle(scanRootId, i);
        }

        return new FileDirIndexStateV1
        {
            LastIndexedGeneration = 123,
            LastIndexedLogSequence = 456,
            DirsById = dirs,
            FilesById = files,
        };
    }
}