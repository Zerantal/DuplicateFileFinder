using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class ScanContext
{
    public required IScanSession Session { get; init; }
    public required ScanRoot ScanRoot { get; init; }
    public required ScanRun Run { get; init; }
    public ScanCheckpoint? Checkpoint { get; init; }  // only for Resume mode
    public required ScanOptions Options { get; init; }
}