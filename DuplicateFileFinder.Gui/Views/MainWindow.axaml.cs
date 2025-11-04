using Avalonia.Controls;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinder.Gui.ViewModels;

namespace DuplicateFileFinder.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var folderSvc = new AvaloniaFolderPickerService(this);
        var fileSvc = new AvaloniaFilePickerService(this);

        DataContext = new MainWindowViewModel(folderSvc, fileSvc);
    }
}