// DuplicateFileFinder.Gui/Services/DialogService.cs

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using LoadingIndicators.Avalonia;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public sealed class DialogService : IDialogService
{
    public Task ShowInfoAsync(string title, string message) => ShowMessageAsync(title, message, "OK");

    public Task ShowErrorAsync(string title, string message) => ShowMessageAsync(title, message, "OK");

    public async Task<string?> ShowTextInputAsync(
        string title,
        string message,
        string? initialText = null,
        string okText = "OK",
        string cancelText = "Cancel")
    {
        var owner = GetOwnerWindow();

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var tcs = new TaskCompletionSource<string?>();

            var window = CreateBasicDialogWindow(title);
            window.Width = 520;
            window.Height = 220;

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

            var input = new TextBox
            {
                Text = initialText ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };

            var okButton = new Button { Content = okText, MinWidth = 80, IsDefault = true };
            var cancelButton = new Button { Content = cancelText, MinWidth = 80, IsCancel = true };

            okButton.Click += (_, _) =>
            {
                tcs.TrySetResult(input.Text);
                window.Close();
            };
            cancelButton.Click += (_, _) =>
            {
                tcs.TrySetResult(null);
                window.Close();
            };

            window.Closed += (_, _) =>
            {
                // If user closes via window chrome, treat as cancel
                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(null);
            };

            buttonsPanel.Children.Add(okButton);
            buttonsPanel.Children.Add(cancelButton);

            content.Children.Add(textBlock);
            content.Children.Add(input);
            content.Children.Add(buttonsPanel);

            window.Content = content;

            await window.ShowDialog(owner);
            return await tcs.Task;
        });
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

    public async Task<bool> ShowActionDialogAsync(
        string title,
        string message,
        Func<CancellationToken, Action<string>, Task<(bool ok, string? error)>> action,
        string okText = "OK",
        string cancelText = "Cancel",
        string workingText = "Working...")
    {
        ArgumentNullException.ThrowIfNull(action);

        var owner = GetOwnerWindow();

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource();

            var window = CreateBasicDialogWindow(title);
            window.Width = 480;
            window.Height = 260;

            var root = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var messageBlock = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

            var progressPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsVisible = false
            };

            var loadingIndicator = new LoadingIndicator
            {
                IsActive = true,
                Mode = LoadingIndicatorMode.ArcsRing,
                SpeedRatio = 1.1,
                Width = 40,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var progressText = new TextBlock { Text = workingText, TextWrapping = TextWrapping.Wrap };

            progressPanel.Children.Add(loadingIndicator);
            progressPanel.Children.Add(progressText);

            var errorBlock = new TextBlock
            {
                IsVisible = false,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.IndianRed
            };

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8
            };

            var okButton = new Button { Content = okText, MinWidth = 80, IsDefault = true };

            var cancelButton = new Button { Content = cancelText, MinWidth = 80, IsCancel = true };

            void SetWorkingText(string text)
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    progressText.Text = text;
                }
                else
                {
                    Dispatcher.UIThread.Post(() => progressText.Text = text);
                }
            }

            async Task RunActionAsync()
            {
                okButton.IsEnabled = false;
                cancelButton.IsEnabled = false;
                window.CanResize = false;

                progressText.Text = workingText;
                errorBlock.Text = string.Empty;
                errorBlock.IsVisible = false;
                progressPanel.IsVisible = true;

                await Task.Yield();

                try
                {
                    var (ok, error) = await Task.Run(
                        () => action(cts.Token, SetWorkingText),
                        cts.Token);

                    if (ok)
                    {
                        tcs.TrySetResult(true);
                        window.Close();
                        return;
                    }

                    progressPanel.IsVisible = false;
                    errorBlock.Text = error ?? "Unknown error.";
                    errorBlock.IsVisible = true;

                    okButton.IsVisible = false;
                    cancelButton.Content = "Close";
                    cancelButton.IsEnabled = true;
                    cancelButton.IsCancel = true;
                }
                catch (OperationCanceledException)
                {
                    progressPanel.IsVisible = false;
                    okButton.IsEnabled = true;
                    cancelButton.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    progressPanel.IsVisible = false;
                    errorBlock.Text = ex.Message;
                    errorBlock.IsVisible = true;

                    okButton.IsVisible = false;
                    cancelButton.Content = "Close";
                    cancelButton.IsEnabled = true;
                    cancelButton.IsCancel = true;
                }
            }

            okButton.Click += async (_, _) => await RunActionAsync();

            cancelButton.Click += (_, _) =>
            {
                tcs.TrySetResult(false);
                window.Close();
            };

            window.Closed += (_, _) =>
            {
                cts.Cancel();

                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(false);

                cts.Dispose();
            };

            root.Children.Add(messageBlock);
            root.Children.Add(progressPanel);
            root.Children.Add(errorBlock);

            buttonsPanel.Children.Add(okButton);
            buttonsPanel.Children.Add(cancelButton);
            root.Children.Add(buttonsPanel);

            window.Content = root;

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
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: { } main
            })
            return main;

        throw new InvalidOperationException("No main window available for dialogs.");
    }

    // -------------------- Internals --------------------

    private static async Task ShowMessageAsync(
        string title,
        string message,
        string okText)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
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
