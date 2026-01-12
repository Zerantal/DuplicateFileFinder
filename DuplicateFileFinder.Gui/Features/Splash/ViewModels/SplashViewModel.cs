using CommunityToolkit.Mvvm.ComponentModel;

namespace DuplicateFileFinder.Gui.Features.Splash.ViewModels;

public sealed class SplashViewModel : ObservableObject
{
    private string _message = "Loading repository…";
    private string _subMessage = "Scanning existing data and applying migrations";

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public string SubMessage
    {
        get => _subMessage;
        set => SetProperty(ref _subMessage, value);
    }
}
