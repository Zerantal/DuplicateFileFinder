// DuplicateFileFinder.Gui/Services/IDialogService.cs

namespace DuplicateFileFinder.Gui.Services;

public interface IDialogService
{
    Task ShowInfoAsync(string title, string message);
    Task ShowErrorAsync(string title, string message);

    /// <summary>
    ///     Show a confirmation dialog. Returns true if the user clicked OK (or equivalent).
    /// </summary>
    Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string okText = "OK",
        string cancelText = "Cancel");

    Task<string?> ShowOpenFolderDialogAsync(
        string title,
        string? initialDirectory = null);

    Task<string?> ShowOpenFileDialogAsync(
        string title,
        string? initialDirectory = null,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null);

    Task<string?> ShowSaveFileDialogAsync(
        string title,
        string? initialDirectory = null,
        string? suggestedFileName = null,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null);
}