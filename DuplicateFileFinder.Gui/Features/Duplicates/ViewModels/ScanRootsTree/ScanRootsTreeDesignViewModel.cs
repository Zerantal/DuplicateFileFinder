using System.Diagnostics;
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

    public ScanRootsRowViewModel? SelectedRow { get; set; }

    public string NameArrow => " ▼";
    public string SizeArrow => string.Empty;
    public string ItemsArrow => " ▼";
    public string FilesArrow => string.Empty;
    public string DupFilesArrow => string.Empty;
    public string DupBytesArrow => string.Empty;

    public ICommand SortByCommand { get; } = new RelayCommand<ScanRootsSortColumn>(_ => { });
    public ICommand ToggleExpandedCommand { get; } = new RelayCommand<ScanRootsRowViewModel?>(_ => { });

    public ScanRootsTreeDesignViewModel()
    {
        // Debugger.Break();
        Debug.WriteLine("Design time constructor");

        var root = CreateRow(
            name: @"C:\Projects",
            fullPath: @"C:\Projects",
            depth: 0,
            isScanRoot: true,
            hasLazyChildren: true,
            isExpanded: true,
            percentOfScanRoot: 82.0,
            totalBytes: 128L * 1024 * 1024 * 1024,
            fileCount: 10128,
            dirCount: 2312,
            duplicateFiles: 1240,
            duplicateBytes: 4700L * 1024 * 1024);

        var child1 = CreateRow(
            name: "src",
            fullPath: @"C:\Projects\src",
            depth: 1,
            isScanRoot: false,
            hasLazyChildren: true,
            isExpanded: false,
            percentOfScanRoot: 44.0,
            totalBytes: 61L * 1024 * 1024 * 1024,
            fileCount: 5112,
            dirCount: 990,
            duplicateFiles: 812,
            duplicateBytes: 2100L * 1024 * 1024);

        var child2 = CreateRow(
            name: @"src\Features",
            fullPath: @"C:\Projects\src\Features",
            depth: 2,
            isScanRoot: false,
            hasLazyChildren: false,
            isExpanded: false,
            percentOfScanRoot: 19.0,
            totalBytes: 14L * 1024 * 1024 * 1024,
            fileCount: 1140,
            dirCount: 268,
            duplicateFiles: 114,
            duplicateBytes: 312L * 1024 * 1024);

        Rows.Add(root);
        Rows.Add(child1);
        Rows.Add(child2);
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
}
