using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage;

namespace DuplicateFileFinderLib.Repository.Plugins.FileDir;

public sealed partial class FileDirIndexPlugin
{
    private string GetStateFilePath() => Path.Combine(_dataDirectory, StateFileName);

    private void SaveState()
    {
        // Snapshot local references to ensure consistency during enumeration.
        var dirs = _dirsById;
        var files = _filesById;

        var state = new FileDirIndexState
        {
            LastIndexedGeneration = _lastIndexedGeneration,
            DirsById = dirs,
            FilesById = files
        };

        var path = GetStateFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        MemoryPackFile.SaveToFile(path, state);
    }

    private bool TryLoadState(long expectedGeneration)
    {
        var path = GetStateFilePath();
        if (!File.Exists(path))
            return false;

        FileDirIndexState? state;
        using (TimingLog.StartPhase("Deserialising FileDirIndexState"))
        {
            if (!MemoryPackFile.TryLoadMapped(path, out state, CancellationToken.None) || state is null)
                return false;
        }

        // Only use the state if it matches the current repo position.
        if (state.LastIndexedGeneration != expectedGeneration)
            return false;

        _dirsById = state.DirsById;
        _filesById = state.FilesById;

        _lastIndexedGeneration = state.LastIndexedGeneration;
        return true;
    }
}
