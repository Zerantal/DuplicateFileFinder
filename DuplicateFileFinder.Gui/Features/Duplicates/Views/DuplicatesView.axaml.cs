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

                var element = treeMap.CurrentNodeUnderPointer;

                if (element is DirTreeMapElement dirElem)
                {
                    vm.TreeMapActions.HoverFolder = dirElem.Dir;
                    return;
                }

                vm.TreeMapActions.HoverFolder = default;
                e.Cancel = true; // prevents empty transparent popup
            };

            cm.Closed += (_, _) =>
            {
                if (DataContext is DuplicatesViewModel vm)
                    vm.TreeMapActions.HoverFolder = default;
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
