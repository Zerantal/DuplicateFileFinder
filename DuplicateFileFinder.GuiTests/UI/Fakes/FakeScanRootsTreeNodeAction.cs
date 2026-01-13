using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public class FakeScanRootsTreeNodeAction(IScanCoordinator scanner) : IScanRootsTreeNodeActions
{

    public Task RescanScanRootAsync(long scanRootId) => throw new System.NotImplementedException();

    public Task RescanFolderAsync(DirHandle dir)
    {
        scanner.RunFolderRescanWithDialogAsync(dir);
        return Task.CompletedTask;
    }

    public Task<bool> TryRemoveScanRootAsync(long scanRootId) => throw new System.NotImplementedException();

    public Task<bool> TrySetScanRootDisplayNameAsync(long scanRootId, string currentLabel) => throw new System.NotImplementedException();

    public Task DeleteFolderAsync(DirHandle dir, string fullPath) => throw new System.NotImplementedException();
}
