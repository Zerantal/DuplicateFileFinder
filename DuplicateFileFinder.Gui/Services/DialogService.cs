// DuplicateFileFinder.Gui/Services/DialogService.cs

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace DuplicateFileFinder.Gui.Services;

public sealed class DialogService : IDialogService
{
    public Task ShowInfoAsync(string title, string message)
    {
        return ShowMessageAsync(title, message, "OK");
    }

    public Task ShowErrorAsync(string title, string message)
    {
        return ShowMessageAsync(title, message, "OK");
    }

    public async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string okText = "OK",
        string cancelText = "Cancel")
    {
        var owner = GetOwnerWindow();

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var tcs = new TaskCompletionSource<bool>();

            var window = CreateBasicDialogWindow(title);
            var content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var textBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            };

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };

            var okButton = new Button { Content = okText, MinWidth = 80 };
            var cancelButton = new Button { Content = cancelText, MinWidth = 80 };

            okButton.Click += (_, _) =>
            {
                tcs.TrySetResult(true);
                window.Close();
            };
            cancelButton.Click += (_, _) =>
            {
                tcs.TrySetResult(false);
                window.Close();
            };

            buttonsPanel.Children.Add(okButton);
            buttonsPanel.Children.Add(cancelButton);

            content.Children.Add(textBlock);
            content.Children.Add(buttonsPanel);
            window.Content = content;

            await window.ShowDialog(owner);
            return await tcs.Task;
        });
    }

    public async Task<string?> ShowFolderPickerDialogAsync(
        string title,
        string? initialDirectory = null)
    {
        var owner = GetOwnerWindow();

        var result = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }

    public async Task<string?> ShowOpenFileDialogAsync(
        string title,
        string? initialDirectory = null,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null)
    {
        var owner = GetOwnerWindow();

        var types = new List<FilePickerFileType>();
        if (filters is { Count: > 0 })
            foreach (var (desc, exts) in filters)
                types.Add(new FilePickerFileType(desc) { Patterns = exts });

        var result = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types
        });

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }


    public async Task<string?> ShowSaveFileDialogAsync(
        string title,
        string? initialDirectory = null,
        string? suggestedFileName = null,
        IReadOnlyList<(string Description, string[] Extensions)>? filters = null)
    {
        var owner = GetOwnerWindow();

        var types = new List<FilePickerFileType>();
        if (filters is { Count: > 0 })
            foreach (var (desc, exts) in filters)
                types.Add(new FilePickerFileType(desc) { Patterns = exts });

        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = types
        });

        return result?.Path.LocalPath;
    }

    public Window GetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } main
            }) return main;

        throw new InvalidOperationException("No main window available for dialogs.");
    }

    // -------------------- Internals --------------------

    private static async Task ShowMessageAsync(
        string title,
        string message,
        string okText)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
            return;

        var owner = desktop.MainWindow;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = CreateBasicDialogWindow(title);

            var content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var textBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button { Content = okText, MinWidth = 80 };
            okButton.Click += (_, _) => window.Close();

            buttonPanel.Children.Add(okButton);

            content.Children.Add(textBlock);
            content.Children.Add(buttonPanel);

            window.Content = content;

            await window.ShowDialog(owner);
        });
    }

    private static Window CreateBasicDialogWindow(string title)
    {
        return new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
    }
}