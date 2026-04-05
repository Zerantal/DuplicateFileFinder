// DuplicateFileFinder.Gui/Services/IDialogService.cs

using Avalonia.Controls;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public interface IDialogService
{
    Task ShowInfoAsync(string title, string message);
    Task ShowErrorAsync(string title, string message);

    public Window GetOwnerWindow();

    /// <summary>
    ///     Show a confirmation dialog. Returns true if the user clicked OK (or equivalent).
    /// </summary>
    Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string okText = "OK",
        string cancelText = "Cancel");

    /// <summary>
    /// Shows a confirmation dialog that stays open while the confirmed async action runs.
    /// Returns true if the action completed successfully, false otherwise/cancelled.
    /// On failure, the dialog stays open and shows the error inline.
    /// </summary>
    Task<bool> ShowActionDialogAsync(
        string title,
        string message,
        Func<CancellationToken, Action<string>, Task<(bool ok, string? error)>> action,
        string okText = "OK",
        string cancelText = "Cancel",
        string workingText = "Working...");

    Task<string?> ShowFolderPickerDialogAsync(
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

    /// <summary>
    /// Shows a simple modal text input dialog. Returns null if cancelled.
    /// </summary>
    Task<string?> ShowTextInputAsync(
        string title,
        string message,
        string? initialText = null,
        string okText = "OK",
        string cancelText = "Cancel");
}
