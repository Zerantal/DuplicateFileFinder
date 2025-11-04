using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DuplicateFileFinder.Gui.Services;

public sealed class AvaloniaFilePickerService(TopLevel topLevel) : IFilePickerService
{
    public async Task<string?> PickOpenFileAsync((string name, string[] extensions)[]? filters = null)
    {
        var provider = topLevel.StorageProvider;
        var results = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import scan (CSV)",
            AllowMultiple = false,
            FileTypeFilter = MakeFilters(filters)
        });
        return results.Count > 0 ? results[0].Path.LocalPath : null;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedFileName, (string name, string[] extensions)[]? filters = null)
    {
        var provider = topLevel.StorageProvider;
        var result = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export scan (CSV)",
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = MakeFilters(filters)
        });
        return result?.Path.LocalPath;
    }

    private static IReadOnlyList<FilePickerFileType>? MakeFilters((string name, string[] extensions)[]? filters)
    {
        if (filters == null || filters.Length == 0) return null;
        return filters.Select(f => new FilePickerFileType(f.name)
        {
            Patterns = f.extensions.Select(ext => "*." + ext).ToArray()
        }).ToArray();
    }
}