using Avalonia.Controls;
using Avalonia.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

using JetBrains.Annotations;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views;

public partial class DuplicatesView : UserControl
{
    public DuplicatesView()
    {
        InitializeComponent();
    }

    [UsedImplicitly]
    private void OnDuplicateSetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DuplicatesViewModel vm)
            return;

        if (sender is not Control c)
            return;

        if (c.DataContext is not DuplicateSetRow row)
            return;

        vm.SelectedSet = row;
    }

}
