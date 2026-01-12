// Features/Controller/Views/Controller/DuplicateGroupsView.axaml.cs

using Avalonia.Controls;
using Avalonia.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.Duplicates;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views.DuplicateGroups;

public partial class DuplicateGroupsView : UserControl
{
    public DuplicateGroupsView()
    {
        InitializeComponent();
    }

    private void OnDuplicateSetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DuplicateGroupsViewModel vm)
            return;

        if (sender is not Control c)
            return;

        if (c.DataContext is not DuplicateSetRow row)
            return;

        vm.SelectedSet = row;
    }
}
