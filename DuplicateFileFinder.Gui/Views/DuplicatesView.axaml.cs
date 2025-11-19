using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DuplicateFileFinder.Gui.ViewModels;
// using NLog;

namespace DuplicateFileFinder.Gui.Views;

public partial class DuplicatesView : UserControl
{
    // private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public DuplicatesView() => InitializeComponent();
    
    private DuplicatesViewModel? Vm => DataContext as DuplicatesViewModel;

    private void OnLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        Vm?.LoadMore();
    }

    private void DataGrid_OnVerticalScroll(object? sender, ScrollEventArgs e)
    {
        if (e.ScrollEventType == ScrollEventType.EndScroll && Vm is { CanLoadMore: true })
        {
            Vm?.LoadMore();
        }
        // Log.Info($"ScrollEvent: ({e.ScrollEventType}, {e.NewValue})");
    }
    
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
}