namespace DuplicateFileFinder.Gui.Infrastructure.Toasts;

public interface IToastService
{
    void Show(string message, ToastKind kind = ToastKind.Info, TimeSpan? duration = null);
}
