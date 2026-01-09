using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;

using DuplicateFileFinder.Gui.Infrastructure.Services;

namespace DuplicateFileFinder.GuiTests.Ui.Fakes;

public sealed class FakeDialogService : IDialogService
{
    public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;
    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

    public Window GetOwnerWindow() => new Window();

    public Task<bool> ShowConfirmationAsync(string title, string message, string okText = "OK", string cancelText = "Cancel")
        => Task.FromResult(true);

    public Task<string?> ShowFolderPickerDialogAsync(string title, string? initialDirectory = null)
        => Task.FromResult<string?>(null);

    public Task<string?> ShowOpenFileDialogAsync(string title, string? initialDirectory = null,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null)
        => Task.FromResult<string?>(null);

    public Task<string?> ShowSaveFileDialogAsync(string title, string? initialDirectory = null,
        string? suggestedFileName = null,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null)
        => Task.FromResult<string?>(null);

    public Task<string?> ShowTextInputAsync(string title, string message, string? initialText = null,
        string okText = "OK", string cancelText = "Cancel")
        => Task.FromResult<string?>(null);
}
