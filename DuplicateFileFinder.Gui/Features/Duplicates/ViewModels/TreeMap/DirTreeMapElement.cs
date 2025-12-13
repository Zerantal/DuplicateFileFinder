using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using DuplicateFileFinder.Gui.Infrastructure.Converters;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class DirTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public DirTreeMapElement(
        DirRecord dir,
        ScanRoot scanRoot,
        DirAggregateStats dirAggregateStats,
        string relativePath,
        double value)
    {
        ScanRoot = scanRoot;
        Stats = dirAggregateStats;

        Name = dir.Name;
        RelativePath = relativePath;
        Value = value;

        Label = VolumeLabel is { Length: > 0 }
            ? Path.Combine(VolumeLabel, RelativePath)
            : RelativePath;
    }

    private DirAggregateStats Stats { get; }

    protected override Func<Control> BuildToolTipFactory()
    {
        // Capture immutable data only
        var name = Name;
        var volume = VolumeLabel ?? "(unknown)";
        var path = RelativePath;
        var bytes = Stats.TotalBytes;
        var files = Stats.FileCount;
        var dirs = Stats.DirCount;

        var bytesFormated = (string?)BytesToHumanConverter.Instance.Convert(
                bytes,
                typeof(string),
                null,
                CultureInfo.CurrentUICulture) ?? $"{bytes} B";

        return () =>
            new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = name, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = "Type: Directory" },
                    new TextBlock { Text = $"Volume: {volume}" },
                    new TextBlock { Text = $"Path: {path}" },
                    new TextBlock { Text = $"Total size: {bytesFormated}" },
                    new TextBlock { Text = $"Files: {files:n0}" },
                    new TextBlock { Text = $"Dirs: {dirs:n0}" }
                }
            };
    }
}