using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DuplicateFileFinder.Gui.Shared.Models;

public class BaseObjectModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
