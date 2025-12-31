using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLibTests.TestUtils.Fakes;

internal sealed class CapturingScanSession : IScanSession
{
    private long _dirCounter = 1000;
    private readonly MethodCounter _methodCounter = new();
    public string? LastFailMessage { get; private set; }
    public bool LastFailCancelled { get; private set; }

    public Dictionary<string, bool> FileDecisionsByName { get; } = new(StringComparer.Ordinal);

    public List<(FileHashToken token, ReadOnlyMemory<byte> hashBytes, string? errorMessage)> HashCompletions { get; } =
        new();

    public DirCursor RootDirCursor { get; private set; } = new(1);

    public void SetPendingDirsProvider(Func<PendingDir[]> getPendingDirs)
    {
        _methodCounter.IncrementMethodCalCount();
    }

    public DirEnumerationContext BeginDirectory(DirCursor parent)
    {
        return new DirEnumerationContext(parent.DirId,
            new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(
                StringComparer.Ordinal),
            new Dictionary<string, (long id, string name, ScanEntryStatus status, long lastSeen)>(
                StringComparer.Ordinal));
    }

    public DirCursor OnDirectoryFound(in ObservedDir dir, ref DirEnumerationContext ctx)
    {
        _methodCounter.IncrementMethodCalCount();
        return new DirCursor(_dirCounter++);
    }

    public FileHashDecision OnFileFound(in ObservedFile file, ref DirEnumerationContext ctx)
    {
        _methodCounter.IncrementMethodCalCount();
        var shouldHash = FileDecisionsByName.GetValueOrDefault(file.Name, true);
        return shouldHash
            ? new FileHashDecision(true,
                new FileHashToken(ctx.ParentDirId, file.Name, file.Size))
            : FileHashDecision.NoHash;
    }

    public void EndDirectory(ref DirEnumerationContext ctx)
    {
    }

    public Task CompleteAsync(CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        return Task.CompletedTask;
    }

    public Task FailAsync(string? errorMessage, bool cancelled, CancellationToken ct = default)
    {
        _methodCounter.IncrementMethodCalCount();
        LastFailMessage = errorMessage;
        LastFailCancelled = cancelled;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _methodCounter.IncrementMethodCalCount();
        return ValueTask.CompletedTask;
    }

    public void OnFileHashCompleted(in FileHashToken token, ReadOnlyMemory<byte> hashBytes, string? errorMessage)
    {
        HashCompletions.Add((token, hashBytes, errorMessage));
    }

    public void SetRootDirId(long dirId)
    {
        RootDirCursor = new DirCursor(dirId);
    }
}
