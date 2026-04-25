using System.Collections.Specialized;
using System.Windows.Input;

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

        const double projectsRootTotalGb = 128.0;
        const double archiveRootTotalGb = 94.0;

        var projectsRoot = CreateRow(
            name: @"C:\Projects",
            fullPath: @"C:\Projects",
            depth: 0,
            isScanRoot: true,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 100.0,
            scanRootTotalBytes: ToBytes(projectsRootTotalGb),
            totalBytes: ToBytes(projectsRootTotalGb),
            fileCount: 28430,
            dirCount: 6420,
            duplicateFiles: 1876,
            duplicateBytes: ToBytes(21.4));
        Rows.Add(projectsRoot);

        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "src", 1, true, true, 13.6, 8643, 2107, 419, 2.4);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"src\core", 2, true, false, 14.2, 8412, 1915, 15, 3.7);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"src\feature-pack", 2, true, true, 10.1, 8434, 1121, 1042, 2.2);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"src\feature-pack\ui", 3, false, false, 4.2, 2610, 280, 211, 0.86);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"src\feature-pack\services", 3, false, false, 3.6, 1974, 194, 144, 0.64);

        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "tests", 1, true, true, 12.4, 5180, 1808, 272, 0.391);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"tests\assets", 2, true, true, 9.8, 3137, 372, 162, 4.2);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"tests\assets\module-04", 3, false, false, 5.6, 2250, 148, 77, 1.3);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"tests\integration", 2, false, false, 0.9, 2744, 550, 101, 0.122);

        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-10", 1, true, true, 9.1, 3160, 504, 188, 1.1);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"workspace-10\src", 2, true, false, 5.2, 1580, 184, 74, 0.62);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"workspace-10\tests", 2, false, false, 2.1, 880, 119, 29, 0.14);

        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-09", 1, false, false, 8.4, 2870, 462, 166, 0.94);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-08", 1, true, false, 7.7, 2610, 411, 143, 0.81);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-07", 1, false, false, 7.1, 2380, 398, 129, 0.74);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "scripts", 1, false, false, 6.1, 2606, 1154, 162, 1.3);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "benchmarks", 1, true, true, 5.6, 8340, 1300, 125, 0.118);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"benchmarks\module-07", 2, true, true, 7.3, 3663, 1493, 489, 2.2);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, @"benchmarks\module-07\feature-52", 3, false, false, 2.8, 1466, 211, 211, 0.91);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-06", 1, true, false, 4.9, 1840, 356, 101, 0.58);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-05", 1, false, false, 4.3, 1520, 284, 88, 0.41);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-04", 1, false, false, 3.6, 1290, 250, 62, 0.33);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-03", 1, false, false, 3.1, 1180, 216, 49, 0.26);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-02", 1, false, false, 2.7, 1040, 191, 38, 0.19);
        AddVisibleRow(@"C:\Projects", projectsRootTotalGb, "workspace-01", 1, false, false, 2.2, 920, 160, 31, 0.12);

        var archiveRoot = CreateRow(
            name: @"D:\Archive",
            fullPath: @"D:\Archive",
            depth: 0,
            isScanRoot: true,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 100.0,
            scanRootTotalBytes: ToBytes(archiveRootTotalGb),
            totalBytes: ToBytes(archiveRootTotalGb),
            fileCount: 6120,
            dirCount: 1448,
            duplicateFiles: 804,
            duplicateBytes: ToBytes(18.6));
        Rows.Add(archiveRoot);

        AddVisibleRow(@"D:\Archive", archiveRootTotalGb, "photos", 1, true, true, 43.3, 2480, 511, 402, 8.2);
        AddVisibleRow(@"D:\Archive", archiveRootTotalGb, @"photos\raw", 2, false, false, 26.7, 1182, 104, 113, 2.1);
        AddVisibleRow(@"D:\Archive", archiveRootTotalGb, @"photos\exports", 2, false, false, 9.1, 904, 88, 251, 4.9);
        AddVisibleRow(@"D:\Archive", archiveRootTotalGb, "video", 1, true, false, 29.6, 640, 213, 294, 7.3);
        AddVisibleRow(@"D:\Archive", archiveRootTotalGb, "documents", 1, false, false, 7.6, 2010, 524, 44, 0.6);

        SelectedRow = projectsRoot;
    }

    private void AddVisibleRow(
        string rootPath,
        double scanRootTotalGb,
        string relativePath,
        int depth,
        bool hasLazyChildren,
        bool isExpanded,
        double totalBytesGb,
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
            percentOfScanRoot: Math.Round(totalBytesGb / scanRootTotalGb * 100.0, 1),
            scanRootTotalBytes: ToBytes(scanRootTotalGb),
            totalBytes: ToBytes(totalBytesGb),
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
        long scanRootTotalBytes,
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
            ScanRootTotalBytes = scanRootTotalBytes
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
