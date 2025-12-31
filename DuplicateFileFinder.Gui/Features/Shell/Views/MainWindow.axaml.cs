using Avalonia.Controls;

using MainWindowViewModel = DuplicateFileFinder.Gui.Features.Shell.ViewModels.MainWindowViewModel;

namespace DuplicateFileFinder.Gui.Features.Shell.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Closed += OnClosed;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.DisposeAsync();
        }
    }
}
