using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DuplicateFileFinder.Util;
using DuplicateFileFinder.Views;
using DuplicateFileFinderLib;

namespace DuplicateFileFinder.Models;

public class FolderModel
{
    static FolderModel()
    {
        var icon = MainWindow.IconManager.GetFolderIcon(IconReader.FolderType.Closed);

        FolderImage =
            Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }

    public FolderModel(FolderNode node)
    {
        Folder = node;
    }

    public ObservableCollection<FolderModel> SubFolders =>
        new(Folder.SubFoldersContainingDuplicates.Select(n => new FolderModel(n)).ToList());

    public string Name => Folder.Name;
    public bool IsFolder => true;

    public static BitmapSource FolderImage { get; }
    public string FullPath => Folder.Path;
    public FolderNode Folder { get; }
}