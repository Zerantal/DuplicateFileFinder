using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using DuplicateFileFinder.Gui.Infrastructure.Converters;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class FileTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public FileTreeMapElement(
        FileRecord file,
        ScanRoot scanRoot,
        string relativePath)
    {
        ScanRoot = scanRoot;
        Name = file.Name;
        Label = file.Name;
        Value = file.Size;

        RelativePath = relativePath; // not including filename
    }

    protected override Func<Control> BuildToolTipFactory()
    {
        var name = Name;
        var volume = VolumeLabel;
        var path = RelativePath;
        var size = Value;

        var sizeFormated = (string?)BytesToHumanConverter.Instance.Convert(
                size,
                typeof(string),
                null,
                CultureInfo.CurrentUICulture) ?? $"{size} B";

        return () =>
            new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = name, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = "Type: _file" },
                    new TextBlock { Text = $"Volume: {volume}" },
                    new TextBlock { Text = $"Path: {path}" },
                    new TextBlock { Text = $"Size: {sizeFormated}" }
                }
            };
    }
}