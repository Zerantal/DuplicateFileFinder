using Avalonia.Controls;
using Avalonia.Input;

using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

using JetBrains.Annotations;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views;

public partial class DuplicatesView : UserControl
{
    public DuplicatesView()
    {
        InitializeComponent();

        var treeMap = this.FindControl<TreeMapControl>("TreeMap");

        if (treeMap?.ContextMenu is { } cm)
        {
            cm.Opening += (_, e) =>
            {
                if (DataContext is not DuplicatesViewModel vm)
                    return;

                var element = treeMap.SelectedNode?.Element;
                if (element is DirTreeMapElement or FileTreeMapElement)
                {
                    vm.TreeMapActions.ContextTarget = element;
                    return;
                }

                vm.TreeMapActions.ContextTarget = null;
                e.Cancel = true;
            };

            cm.Closed += (_, _) =>
            {
                if (DataContext is DuplicatesViewModel vm)
                    vm.TreeMapActions.ContextTarget = null;
            };
        }
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
