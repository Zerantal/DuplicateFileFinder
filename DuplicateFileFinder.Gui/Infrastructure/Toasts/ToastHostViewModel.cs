using System.Collections.ObjectModel;

namespace DuplicateFileFinder.Gui.Infrastructure.Toasts;

public sealed class ToastHostViewModel
{
    public ObservableCollection<ToastItemViewModel> Items { get; } = new();

    internal void Add(ToastItemViewModel toast) => Items.Add(toast);
    internal void Remove(ToastItemViewModel toast) => Items.Remove(toast);
}
