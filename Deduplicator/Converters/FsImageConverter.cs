using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Deduplicator.Models;
using Deduplicator.Util;
using DuplicateFileFinder.Views;

namespace Deduplicator.Converters;

internal class FsImageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        Debug.Assert(value is FileModel or FolderModel);

        string path;
        bool isFolder;
        if (value is FileModel node)
        {
            path = node.Path;
            isFolder = node.IsFolder;
        }
        else
        {
            var folder = (FolderModel) value;
            path = folder.FullPath;
            isFolder = folder.IsFolder;
        }

        var icon = isFolder ? MainWindow.IconManager.GetFolderIcon(IconReader.FolderType.Open) : MainWindow.IconManager.GetFileIcon(path);

        Debug.Assert(icon != null, nameof(icon) + " != null");

        return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}