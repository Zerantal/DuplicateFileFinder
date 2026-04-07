using System;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

using DuplicateFileFinder.Gui;

namespace DuplicateFileFinder.GuiTests.Ui;

public sealed class AvaloniaHeadlessFixture : IDisposable
{
    private readonly Thread _uiThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startupError;

    public AvaloniaHeadlessFixture()
    {
        _uiThread = new Thread(UiThreadMain)
        {
            IsBackground = true,
            Name = "AvaloniaUIThread"
        };

        _uiThread.Start();

        // Wait for Avalonia to be ready (or fail)
        _ready.Wait();

        if (_startupError != null)
            throw new InvalidOperationException("Failed to start Avalonia headless UI thread.", _startupError);
    }

    private void UiThreadMain()
    {
        try
        {
            if (Avalonia.Application.Current == null)
            {
                AppBuilder.Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions
                    {
                        UseHeadlessDrawing = true
                    })
                    .SetupWithoutStarting();
            }

            _ready.Set();

            // Run dispatcher loop until disposed
            Dispatcher.UIThread.MainLoop(_cts.Token);
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
    }

    public Task RunOnUiThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    public Task<T> RunOnUiThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    public Task RunOnUiThreadAsync(Func<Task> func)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                await func().ConfigureAwait(true);
                tcs.SetResult(null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    public void Dispose()
    {
        _cts.Cancel();

        // Nudge loop to wake if idle
        Dispatcher.UIThread.Post(() => { });

        if (!_uiThread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Avalonia UI thread did not shut down in time.");

        _cts.Dispose();
        _ready.Dispose();
    }
}
