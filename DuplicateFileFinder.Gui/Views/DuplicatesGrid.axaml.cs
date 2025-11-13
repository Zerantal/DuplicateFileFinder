using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DuplicateFileFinder.Gui.Views;

public partial class DuplicatesGrid : UserControl
{
    public DuplicatesGrid() => InitializeComponent();

    private void OnCollapseDetailsClick(object? sender, RoutedEventArgs e)
    {
        // Clears selection, which collapses the details row.
        PartGrid.SelectedItem = null;
    }
}