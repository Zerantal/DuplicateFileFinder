using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using DuplicateFileFinder.Gui.Infrastructure.Toasts;
using DuplicateFileFinder.Gui.Infrastructure.Toasts.Views;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

public sealed class ToastHostSmokeTests
{
    [AvaloniaFact]
    public void ToastHost_RendersItemsControl()
    {
        var vm = new ToastHostViewModel();
        vm.Items.Add(new ToastItemViewModel("Hello", ToastKind.Info));

        var view = new ToastHost { DataContext = vm };
        LayoutTestHelpers.DoLayout(view);

        var items = view.FindControl<ItemsControl>("ToastItems");
        Assert.NotNull(items);
        Assert.Equal(1, items.ItemCount);
    }
}
