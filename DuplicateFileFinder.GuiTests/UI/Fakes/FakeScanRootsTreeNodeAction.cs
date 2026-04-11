using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public class FakeScanRootsTreeNodeAction(IScanCoordinator scanner) : IScanRootsTreeNodeActions
{

    public Task RescanScanRootAsync(ScanRootId scanRootId) => throw new System.NotImplementedException();

    public Task RescanFolderAsync(DirHandle dir)
    {
        scanner.RunFolderRescanWithDialogAsync(dir);
        return Task.CompletedTask;
    }

    public Task<bool> TryRemoveScanRootAsync(ScanRootId scanRootId) => throw new System.NotImplementedException();

    public Task<bool> TrySetScanRootDisplayNameAsync(ScanRootId scanRootId, string currentLabel) => throw new System.NotImplementedException();

    public Task CopyPathAsync(string fullPath) => Task.CompletedTask;
}
