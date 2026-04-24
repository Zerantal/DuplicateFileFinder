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

        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: "src",
            depth: 1,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 11.8,
            totalBytes: 13600,
            fileCount: 8643,
            dirCount: 2107,
            duplicateFiles: 419,
            duplicateBytesGb: 2.4);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"src\core",
            depth: 2,
            hasLazyChildren: true,
            isExpanded: false,
            percentOfScanRoot: 5.1,
            totalBytes: 14200,
            fileCount: 8412,
            dirCount: 1915,
            duplicateFiles: 15,
            duplicateBytesGb: 3.7);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"src\feature-pack",
            depth: 2,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 8.4,
            totalBytes: 10100,
            fileCount: 8434,
            dirCount: 1121,
            duplicateFiles: 1042,
            duplicateBytesGb: 2.2);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"src\feature-pack\ui",
            depth: 3,
            hasLazyChildren: false,
            isExpanded: false,
            percentOfScanRoot: 3.7,
            totalBytes: 4200,
            fileCount: 2610,
            dirCount: 280,
            duplicateFiles: 211,
            duplicateBytesGb: 0.86);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"src\feature-pack\services",
            depth: 3,
            hasLazyChildren: false,
            isExpanded: false,
            percentOfScanRoot: 2.9,
            totalBytes: 3600,
            fileCount: 1974,
            dirCount: 194,
            duplicateFiles: 144,
            duplicateBytesGb: 0.64);

        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: "tests",
            depth: 1,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 11.0,
            totalBytes: 1300,
            fileCount: 5180,
            dirCount: 1808,
            duplicateFiles: 272,
            duplicateBytesGb: 0.391);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"tests\integration",
            depth: 2,
            hasLazyChildren: false,
            isExpanded: false,
            percentOfScanRoot: 3.0,
            totalBytes: 900,
            fileCount: 2744,
            dirCount: 550,
            duplicateFiles: 101,
            duplicateBytesGb: 0.122);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"tests\assets",
            depth: 2,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 3.8,
            totalBytes: 14500,
            fileCount: 3137,
            dirCount: 372,
            duplicateFiles: 162,
            duplicateBytesGb: 4.2);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"tests\assets\module-04",
            depth: 3,
            hasLazyChildren: false,
            isExpanded: false,
            percentOfScanRoot: 1.9,
            totalBytes: 5600,
            fileCount: 2250,
            dirCount: 148,
            duplicateFiles: 77,
            duplicateBytesGb: 1.3);

        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: "scripts",
            depth: 1,
            hasLazyChildren: false,
            isExpanded: false,
            percentOfScanRoot: 9.3,
            totalBytes: 6100,
            fileCount: 2606,
            dirCount: 1154,
            duplicateFiles: 162,
            duplicateBytesGb: 1.3);

        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: "benchmarks",
            depth: 1,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 14.9,
            totalBytes: 5600,
            fileCount: 8340,
            dirCount: 1300,
            duplicateFiles: 125,
            duplicateBytesGb: 0.118);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"benchmarks\module-07",
            depth: 2,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 9.6,
            totalBytes: 7300,
            fileCount: 3663,
            dirCount: 1493,
            duplicateFiles: 489,
            duplicateBytesGb: 2.2);
        AddVisibleRow(
            rootPath: @"C:\Projects",
            relativePath: @"benchmarks\module-07\feature-52",
            depth: 3,
            hasLazyChildren: false,
            isExpanded: false,
            percentOfScanRoot: 5.2,
            totalBytes: 2800,
            fileCount: 1466,
            dirCount: 211,
            duplicateFiles: 211,
            duplicateBytesGb: 0.91);

        for (var i = 0; i < 18; i++)
        {
            var groupName = $"workspace-{i + 1:D2}";
            var percent = 12.0 + i * 1.7;
            var totalBytes = 2200 + i * 410;
            var files = 900 + i * 180;
            var dirs = 140 + i * 21;
            var dupFiles = 30 + i * 9;
            var dupBytes = 240 + i * 70;
            var isExpanded = i % 3 == 0;

            AddVisibleRow(
                rootPath: @"C:\Projects",
                relativePath: groupName,
                depth: 1,
                hasLazyChildren: true,
                isExpanded: isExpanded,
                percentOfScanRoot: percent,
                totalBytes: totalBytes,
                fileCount: files,
                dirCount: dirs,
                duplicateFiles: dupFiles,
                duplicateBytesGb: dupBytes / 1024.0);

            if (!isExpanded)
                continue;

            AddVisibleRow(
                rootPath: @"C:\Projects",
                relativePath: $@"{groupName}\src",
                depth: 2,
                hasLazyChildren: i % 2 == 0,
                isExpanded: false,
                percentOfScanRoot: percent * 0.46,
                totalBytes: totalBytes * 0.54,
                fileCount: (int)(files * 0.52),
                dirCount: (int)(dirs * 0.40),
                duplicateFiles: (int)(dupFiles * 0.35),
                duplicateBytesGb: dupBytes * 0.48 / 1024.0);

            AddVisibleRow(
                rootPath: @"C:\Projects",
                relativePath: $@"{groupName}\tests",
                depth: 2,
                hasLazyChildren: false,
                isExpanded: false,
                percentOfScanRoot: percent * 0.27,
                totalBytes: totalBytes * 0.28,
                fileCount: (int)(files * 0.31),
                dirCount: (int)(dirs * 0.24),
                duplicateFiles: (int)(dupFiles * 0.22),
                duplicateBytesGb: dupBytes * 0.19 / 1024.0);
        }

        SelectedRow = root;
    }

    private void AddVisibleRow(
        string rootPath,
        string relativePath,
        int depth,
        bool hasLazyChildren,
        bool isExpanded,
        double percentOfScanRoot,
        double totalBytes,
        int fileCount,
        int dirCount,
        long duplicateFiles,
        double duplicateBytesGb)
    {
        var fullPath = $@"{rootPath}\{relativePath}";

        Rows.Add(CreateRow(
            name: relativePath.Split('\\')[^1],
            fullPath: fullPath,
            depth: depth,
            isScanRoot: false,
            hasLazyChildren: hasLazyChildren,
            isExpanded: isExpanded,
            percentOfScanRoot: Math.Round(percentOfScanRoot, 1),
            totalBytes: ToBytes(totalBytes),
            fileCount: fileCount,
            dirCount: dirCount,
            duplicateFiles: duplicateFiles,
            duplicateBytes: ToBytes(duplicateBytesGb)));
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

    private static long ToBytes(double gigabytes) =>
        (long)Math.Round(gigabytes * 1024 * 1024 * 1024);

    private void RowsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasRows));
}
