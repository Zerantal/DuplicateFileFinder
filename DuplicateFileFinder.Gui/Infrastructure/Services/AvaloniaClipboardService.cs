using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var quoted = QuoteIfNeeded(text);

        if (Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            var clipboard = window.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(quoted);
            }
        }
    }

    private static string QuoteIfNeeded(string path)
    {
        // Always quote — predictable and safe across shells
        if (path.StartsWith('"') && path.EndsWith('"'))
            return path;

        return $"\"{path}\"";
    }
}
