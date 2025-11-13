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

        var folderSvc = new AvaloniaFolderPickerService(this);
        var fileSvc = new AvaloniaFilePickerService(this);
        
        var appName = "DuplicateFileFinder";
        var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName);
        var repo = Repo.Open(Path.Combine(appDir, "repo"));
        DataContext = new MainWindowViewModel(repo, folderSvc, fileSvc);

    }
}