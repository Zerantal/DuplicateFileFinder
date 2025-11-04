using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DuplicateFileFinder.Gui.Services;

public class AvaloniaFolderPickerService(TopLevel top) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(CancellationToken ct = default)
    {
        var result = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select location to scan"
        });
        return (result is { Count: > 0 }) ? result[0].Path.LocalPath : null;
    }
}