// ViewModels/SplashViewModel.cs

using ReactiveUI;

namespace DuplicateFileFinder.Gui.Features.Scanning.ViewModels;

public sealed class SplashViewModel : ReactiveObject
{
    private string _message    = "Loading repository…";
    private string _subMessage = "Scanning existing data and applying migrations";

    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public string SubMessage
    {
        get => _subMessage;
        set => this.RaiseAndSetIfChanged(ref _subMessage, value);
    }
}