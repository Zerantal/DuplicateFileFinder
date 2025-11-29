using Avalonia.Controls;
using Avalonia.Interactivity;
using DuplicateFileFinder.Gui.ViewModels;

namespace DuplicateFileFinder.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var appName = "DuplicateFileFinder";
        var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);
        var repoDir = Path.Combine(appDir, "repo");

        DataContext = await MainWindowViewModel.CreateMainWindowAsync(repoDir);
    }
}