using DuplicateFileFinderLib.Repository.Core;

namespace DuplicateFileFinderLib.Repository.Interfaces;

public interface IRepoEventSink
{
    // Must be fast and non-blocking. Implementations enqueue to their own channel.
    void Post(RepoEvent evt);
}