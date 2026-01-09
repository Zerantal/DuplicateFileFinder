using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Threading;
using DuplicateFileFinder.Gui.Infrastructure.Toasts;
using DuplicateFileFinder.Gui.Infrastructure.Toasts.Views;
using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

[Collection("AvaloniaUI")]
public sealed class ToastHostSmokeTests
{
    [Fact]
    public async Task ToastHost_RendersItemsControl()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = new ToastHostViewModel();
            vm.Items.Add(new ToastItemViewModel("Hello", ToastKind.Info));

            var view = new ToastHost { DataContext = vm };
            LayoutTestHelpers.DoLayout(view);

            var items = view.FindControl<ItemsControl>("ToastItems");
            Assert.NotNull(items);
            Assert.Equal(1, items.ItemCount);
        });
    }
}
