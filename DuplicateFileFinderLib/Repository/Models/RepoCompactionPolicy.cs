// DuplicateFileFinderLib/Repo/Models/RepoCompactionPolicy.cs
namespace DuplicateFileFinderLib.Repository.Models;

public sealed class RepoCompactionPolicy
{
    // Trigger when log_bytes / snapshot_bytes ≥ RatioThreshold
    public double RatioThreshold { get; init; } = 2.0;

    // Also require at least this many bytes of deltas
    public long MinLogBytes { get; init; } = 16 * 1024 * 1024; // 16 MB

    // And at least this many delta files
    public int MinDeltaCount { get; init; } = 4;
}