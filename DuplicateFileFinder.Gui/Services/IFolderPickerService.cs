// Services/IFolderPickerService.cs

namespace DuplicateFileFinder.Gui.Services;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(CancellationToken ct = default);
}