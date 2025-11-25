using Avalonia.Controls;
using Avalonia.Input;
using DuplicateFileFinder.Gui.Models;
using DuplicateFileFinder.Gui.ViewModels;
using FolderNodeViewModel = DuplicateFileFinder.Gui.ViewModels.FolderNodeViewModel;

// using NLog;

namespace DuplicateFileFinder.Gui.Views;

public partial class DuplicatesView : UserControl
{
    // private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public DuplicatesView()
    {
        InitializeComponent();
    }

    private DuplicatesViewModel? Vm => DataContext as DuplicatesViewModel;
    
    private void OnFolderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is null)
            return;
    
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is FolderNodeViewModel node)
        {
            Vm.SelectedFolderPrefix = node.FullPath;
        }
        else
        {
            Vm.SelectedFolderPrefix = null;
        }
    }
    
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