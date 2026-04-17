using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;

namespace DuplicateFileFinderLib.Repository.Plugins.Tree;

public sealed partial class TreeIndexPlugin
{
    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        var roots = _roots;

        var state = new TreeIndexState { LastIndexedGeneration = _lastIndexedGeneration, Roots = roots };

        var path = GetStateFilePath();

        MemoryPackFile.SaveToFile(path, state);
    }

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        TreeIndexState? state;
        using (TimingLog.StartPhase("Deserialising TreeIndex state"))
        {
            if (!MemoryPackFile.TryLoadMapped(path, out state, CancellationToken.None) || state is null)
                return false;
        }

        if (state.LastIndexedGeneration != expectedGeneration)
            return false;

        _lastIndexedGeneration = state.LastIndexedGeneration;
        _roots = state.Roots;

        return true;
    }
}
