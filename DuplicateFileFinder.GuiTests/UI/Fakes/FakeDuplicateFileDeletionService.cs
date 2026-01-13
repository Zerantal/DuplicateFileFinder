using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeDuplicateFileDeletionService : IDuplicateFileDeletionService
{
    public DuplicateFileDeletionResult NextResult { get; set; } =
        new(false, DuplicateFileDeletionFailure.CancelledByUser);

    public List<(long FileId, string FullPath)> Calls { get; } = [];

    public Task<DuplicateFileDeletionResult> DeleteAsync(long fileId, string fullPath, CancellationToken ct)
    {
        Calls.Add((fileId, fullPath));
        return Task.FromResult(NextResult);
    }
}
