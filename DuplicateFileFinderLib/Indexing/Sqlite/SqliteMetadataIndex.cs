using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace DuplicateFileFinderLib.Indexing.Sqlite;

public sealed class SqliteMetadataIndex : IMetadataIndex
{
    private SqliteConnection _con = default!;
    private SqliteTransaction? _tx;
    public VolumeId Volume { get; private set; }

    public async Task OpenOrCreateAsync(string dbPath, CancellationToken ct)
    {
        _con = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        await _con.OpenAsync(ct);
        using var cmd = _con.CreateCommand();
        cmd.CommandText = @"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
        await cmd.ExecuteNonQueryAsync(ct);
        await CreateSchemaAsync(ct);
    }

    private async Task CreateSchemaAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<IndexStats> GetStatsAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task BeginDirectoryBatchAsync(string dirPath, DateTimeOffset dirMtime, long? hint, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public ValueTask UpsertFileAsync(FileEntryMeta meta, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task EndDirectoryBatchAsync(CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<FileEntryMeta> EnumerateAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        yield break;
        throw new NotImplementedException();
        
        // var cmd = _con.CreateCommand();
        // cmd.CommandText = @"SELECT d.path, f.name, f.size_bytes, f.mtime_ns, f.ctime_ns, f.inode, f.mode
        //                     FROM file f JOIN dir d ON f.dir_id=d.id";
        // using var r = await cmd.ExecuteReaderAsync(ct);
        // while (await r.ReadAsync(ct))
        // {
        //     yield return new FileEntryMeta(
        //         r.GetString(0), r.GetString(1),
        //         r.GetInt64(2),
        //         DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)/1_000_000),
        //         DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)/1_000_000),
        //         r.IsDBNull(5) ? null : r.GetFieldValue<ulong>(5),
        //         r.GetInt32(6));
        // }
    }

    public async Task<DirectoryDiff> DiffDirectoryAsync(string dirPath, DateTimeOffset dirMtime, CancellationToken ct)
    {
        // quick path: if stored mtime == dirMtime and entry_count matches, return empty diff
        // else read current entries with direct reader supplied by caller and compare to DB sets
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync() => _con.DisposeAsync();
}