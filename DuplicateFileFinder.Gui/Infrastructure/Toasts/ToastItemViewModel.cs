namespace DuplicateFileFinder.Gui.Infrastructure.Toasts;

public sealed class ToastItemViewModel(string message, ToastKind kind)
{
    public string Message { get; } = message;
    public ToastKind Kind { get; } = kind;
}
