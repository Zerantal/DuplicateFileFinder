using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;

using DuplicateFileFinder.Gui.Infrastructure.Services;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeDialogService : IDialogService
{
    public bool NextConfirmResult { get; set; } = true;

    public List<(string Title, string Message, string OkText, string CancelText)> Confirmations { get; } = [];
    public List<(string Title, string Message)> Errors { get; } = [];

    public string? NextTextInput { get; set; }

    public bool NextProgressConfirmationResult { get; set; } = true;

    public List<(string Title, string Message, string OkText, string CancelText, string WorkingText)>
        ProgressConfirmations { get; } = [];

    public List<string> LastProgressPhaseTexts { get; } = [];

    public (bool ok, string? error)? LastProgressActionResult { get; private set; }

    public readonly List<(
        string Title,
        string Message,
        string? InitialText,
        string OkText,
        string CancelText)> TextInputs = [];

    public Window GetOwnerWindow() => throw new NotImplementedException();

    public Task<bool> ShowConfirmationAsync(string title, string message, string okText, string cancelText)
    {
        Confirmations.Add((title, message, okText, cancelText));
        return Task.FromResult(NextConfirmResult);
    }

    public async Task<bool> ShowActionDialogAsync(
        string title,
        string message,
        Func<CancellationToken, Action<string>, Task<(bool ok, string? error)>> action,
        string okText = "OK",
        string cancelText = "Cancel",
        string workingText = "Working...")
    {
        ProgressConfirmations.Add((title, message, okText, cancelText, workingText));

        if (!NextProgressConfirmationResult)
            return false;

        LastProgressPhaseTexts.Clear();

        LastProgressActionResult = await action(
            CancellationToken.None,
            text => LastProgressPhaseTexts.Add(text));

        return LastProgressActionResult.Value.ok;
    }

    public Task<string?> ShowFolderPickerDialogAsync(string title, string? initialDirectory = null) =>
        Task.FromResult<string?>(Path.Combine(initialDirectory ?? string.Empty, "folder"));

    public Task<string?> ShowOpenFileDialogAsync(
        string title,
        string? initialDirectory,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null) =>
        throw new NotImplementedException();

    public Task<string?> ShowSaveFileDialogAsync(
        string title,
        string? initialDirectory = null,
        string? suggestedFileName = null,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null) =>
        throw new NotImplementedException();

    public Task<string?> ShowTextInputAsync(
        string title,
        string message,
        string? initialText = null,
        string okText = "OK",
        string cancelText = "Cancel")
    {
        TextInputs.Add((title, message, initialText, okText, cancelText));
        return Task.FromResult(NextTextInput);
    }

    public Task ShowInfoAsync(string title, string message) => throw new NotImplementedException();

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add((title, message));
        return Task.CompletedTask;
    }
}
