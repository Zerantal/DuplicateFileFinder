using Avalonia.Controls;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinder.Gui.ViewModels;
using DuplicateFileFinderLib.Repository;

namespace DuplicateFileFinder.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        
        var appName = "DuplicateFileFinder";
        var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);
        
        var repo = Repo.Open(Path.Combine(appDir, "repo"));
        var dialogService = new DialogService();
        var scanEngine = new DuplicateFileFinderLib.Core.DuplicateFileFinder(repo);
        var scanCoordinator = new ScanCoordinator(repo, scanEngine);
        DataContext = new MainWindowViewModel(repo, scanCoordinator, dialogService);

    }
}