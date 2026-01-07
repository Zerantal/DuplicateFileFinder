using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Core;

/// <summary>
/// Run time state of scanning
/// </summary>
internal sealed class ScanFrontier
{
    private readonly Stack<PendingDir> _stack = new();

    public int Count => _stack.Count;

    public static ScanFrontier Create(
        string rootPath,
        DirCursor rootCursor,
        ScanCheckpoint? checkpoint,
        PendingDir? startDir = null)
    {
        var f = new ScanFrontier();

        if (checkpoint is { PendingDirs.Length: > 0 })
        {
            // Resume
            for (int i = 0; i < checkpoint.PendingDirs.Length; i++)
                f._stack.Push(checkpoint.PendingDirs[i]);
        }
        else
        {
            // Fresh scan starts at root or a specified subtree
            f._stack.Push(startDir ?? new PendingDir(rootCursor.DirId, rootPath));
        }

        return f;
    }

    public void Push(PendingDir d) => _stack.Push(d);
    public PendingDir Pop() => _stack.Pop();

    // Snapshot for checkpointing (time-based gating is done in ScanSession)
    public PendingDir[] Snapshot() => _stack.ToArray();
}
