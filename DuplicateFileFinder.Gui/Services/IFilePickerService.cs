namespace DuplicateFileFinder.Gui.Services;

public interface IFilePickerService
{
    Task<string?> PickOpenFileAsync((string name, string[] extensions)[]? filters = null);
    Task<string?> PickSaveFileAsync(string suggestedFileName, (string name, string[] extensions)[]? filters = null);
}