using Avalonia.Controls;
using DuplicateFileFinder.Gui.ViewModels;

namespace DuplicateFileFinder.Gui.Views;

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