using System.Windows.Input;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

// ReSharper disable once PartialTypeWithSinglePart
public sealed partial class ScanRootsTreeDesignViewModel : ObservableObject, IScanRootsTreeViewContext
{
    public BulkObservableCollection<ScanRootsRowViewModel> Rows { get; } = new();
    public bool HasRows => Rows.Count > 0;

    public ScanRootsRowViewModel? SelectedRow { get; set; }

    public ScanRootsSortColumn SortColumn => ScanRootsSortColumn.Size;
    public bool SortDescending => true;

    public string NameArrow => string.Empty;
    public string SizeArrow => "▼";
    public string ItemsArrow => string.Empty;
    public string FilesArrow => string.Empty;
    public string DupFilesArrow => string.Empty;
    public string DupBytesArrow => string.Empty;

    public ICommand SortByCommand { get; } = new RelayCommand<ScanRootsSortColumn>(_ => { });
    public ICommand ToggleExpandedCommand { get; } = new RelayCommand<ScanRootsRowViewModel?>(_ => { });

    public ScanRootsTreeDesignViewModel()
    {
        Rows.CollectionChanged += RowsOnCollectionChanged;

        const long scanRootBytes = 128L * 1024 * 1024 * 1024;
        var rng = new Random(1337);

        var root = CreateRow(
            name: @"C:\Projects",
            fullPath: @"C:\Projects",
            depth: 0,
            isScanRoot: true,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 82.0,
            totalBytes: scanRootBytes,
            fileCount: 10128,
            dirCount: 2312,
            duplicateFiles: 1240,
            duplicateBytes: 4700L * 1024 * 1024);

        Rows.Add(root);

        // Add enough rows to guarantee scrolling in design preview.
        var topFolders = new[]
        {
            "src", "tools", "docs", "tests", "assets", "samples", "scripts", "benchmarks"
        };

        for (var i = 0; i < 120; i++)
        {
            var depth = i % 3 + 1; // 1..3
            var top = topFolders[i % topFolders.Length];
            var segmentA = $"module-{i % 24:D2}";
            var segmentB = $"feature-{rng.Next(1, 60):D2}";

            var name = depth switch
            {
                1 => top,
                2 => $@"{top}\{segmentA}",
                _ => $@"{top}\{segmentA}\{segmentB}"
            };

            var fullPath = $@"C:\Projects\{name}";
            var percent = Math.Round(rng.NextDouble() * 100.0, 1);
            var bytes = rng.NextInt64(80L * 1024 * 1024, 16L * 1024 * 1024 * 1024);
            var files = rng.Next(40, 9000);
            var dirs = rng.Next(8, 2200);
            var dupFiles = rng.Next(0, Math.Max(1, files / 4));
            var dupBytes = rng.NextInt64(0, Math.Max(1L, bytes / 3));

            Rows.Add(CreateRow(
                name: name,
                fullPath: fullPath,
                depth: depth,
                isScanRoot: false,
                hasLazyChildren: depth < 3 && rng.NextDouble() > 0.35,
                isExpanded: false,
                percentOfScanRoot: percent,
                totalBytes: bytes,
                fileCount: files,
                dirCount: dirs,
                duplicateFiles: dupFiles,
                duplicateBytes: dupBytes));
        }

        SelectedRow = root;
    }

    private static ScanRootsRowViewModel CreateRow(
        string name,
        string fullPath,
        int depth,
        bool isScanRoot,
        bool hasLazyChildren,
        bool isExpanded,
        double percentOfScanRoot,
        long totalBytes,
        int fileCount,
        int dirCount,
        long duplicateFiles,
        long duplicateBytes)
    {
        var node = new ScanRootsTreeNode
        {
            Dir = DirHandle.Invalid,
            ScanRootId = 0,
            IsScanRoot = isScanRoot,
            Name = name,
            FullPath = fullPath,
            HasLazyChildren = hasLazyChildren,
            ChildrenMaterialized = true,
            IsAvailable = true,
            TotalBytes = totalBytes,
            FileCount = fileCount,
            DirCount = dirCount,
            DuplicateFiles = duplicateFiles,
            DuplicateBytes = duplicateBytes,
            PercentOfScanRoot = percentOfScanRoot,
            ScanRootTotalBytes = 128L * 1024 * 1024 * 1024
        };

        var row = new ScanRootsRowViewModel(node, actions: null, deletionService: null, depth)
        {
            IsExpanded = isExpanded
        };

        return row;
    }

    private void RowsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasRows));
}
