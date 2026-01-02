using Avalonia.Controls;
using Avalonia.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

using JetBrains.Annotations;

// using NLog;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views;

public partial class DuplicatesView : UserControl
{
    // private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public DuplicatesView()
    {
        InitializeComponent();
    }

    // private DuplicatesViewModel? Vm => DataContext as DuplicatesViewModel;

    // [UsedImplicitly]
    // private void OnFolderSelected(object? sender, SelectionChangedEventArgs e)
    // {
    //     if (Vm is null)
    //         return;
    //
    //     if (e.AddedItems.Count > 0 && e.AddedItems[0] is FolderNodeViewModel node)
    //     {
    //         Vm.SelectedFolderPrefix = node.FullPath;
    //     }
    //     else
    //     {
    //         Vm.SelectedFolderPrefix = null;
    //     }
    // }

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
