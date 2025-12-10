using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLibTests.TestUtils.Fakes;

internal sealed class CapturingScanSession : IScanSession
{
    private long _dirCounter = 1;
    public ScanRun Run { get; }
    
    public long ScanSequence => Run.ScanSequence;
    public string RootPath => Run.RootPath;
    public DirRecord RootDir { get; init; }
    
    public readonly List<ObservedDir> ObservedDirectories = new();
    public readonly List<ObservedFile> ObservedFiles = new();

    public CapturingScanSession(string rootPath = "/root")
    {
        Run = new ScanRun
        {
            ScanSequence = 1,
            RootPath = rootPath,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress,
            ScanRootId = 88,
            Operation = ScanOperation.FullScan,
        };
            
        RootDir = new DirRecord
        {
            DirId = 121,
            ParentDirId = null,
            Name = "",
            LastSeenScanSequence = 99,
            Status = ScanEntryStatus.None, // “known root, not yet enumerated”
            ErrorMessage = null
        };
    }
    
    public List<ObservedDir> FinalDirs => ObservedDirectories.GroupBy(d => d.FullPath).Select(g => g.Last()).ToList();
    public List<ObservedFile> FinalFiles => ObservedFiles.GroupBy(f => f.FullPath).Select(f => f.Last()).ToList();
        
    public int FlushCallCount { get; private set; }
    public int CompleteCallCount { get; private set; }
    public List<(string? Error, bool Cancelled)> FailCalls { get; } = new();
    public int DisposeCallCount { get; private set; }
    
    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        return ValueTask.CompletedTask;
    }
    
    public long AddOrUpdateDirectory(DirRecord dir)
    {
        dir = dir with { DirId = _dirCounter++ };
        var parentId = dir.ParentDirId;
        string dirPath;
        if (parentId is null)
            dirPath = RootPath;
        else
        {
            dirPath = ObservedDirectories.FirstOrDefault(d => d.DirRecord.DirId == parentId)?.FullPath ??
                      RootPath;   
        }
            
        var fullPath = Path.Combine(dirPath, dir.Name);
        ObservedDirectories.Add(new ObservedDir(fullPath, dir));
            
        return dir.DirId;
    }
    
    public void AddOrUpdateFile(ref FileRecord file)
    {
        var dirId = file.DirId;
        var dirPath = ObservedDirectories.FirstOrDefault(d => d.DirRecord.DirId == dirId)?.FullPath ??
                      RootPath;
            
        var fullPath = Path.Combine(dirPath, file.Name);
        ObservedFiles.Add(new ObservedFile(fullPath, file));
    }
    
    public Task FlushProgressAsync(CancellationToken cancellationToken = default)
    {
        FlushCallCount++;
        return Task.CompletedTask;
    }
    
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        CompleteCallCount++;
        return Task.CompletedTask;
    }
    
    public Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
    {
        FailCalls.Add((errorMessage, cancelled));
        return Task.CompletedTask;
    }
}
    
internal sealed class ObservedDir(string fullPath, DirRecord dir)
{
    public string FullPath { get; } = fullPath;
    public DirRecord DirRecord { get; } = dir;
        
    public ScanEntryStatus Status => DirRecord.Status;
}
internal sealed class ObservedFile(string fullPathOnDisk, FileRecord file)
{
    public string FullPath { get; } = fullPathOnDisk;
        
    public FileRecord FileRecord { get; } = file;
        
    public ScanEntryStatus Status => FileRecord.Status;
        
    public HashKey? Hash => FileRecord.Hash;
    public long Size => FileRecord.Size;
    public DateTimeOffset? Created => FileRecord.Created;
}