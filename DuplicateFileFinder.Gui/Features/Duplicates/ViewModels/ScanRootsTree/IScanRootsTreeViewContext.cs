using System.Windows.Input;

using DuplicateFileFinder.Gui.Infrastructure.Util;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

public interface IScanRootsTreeViewContext
{
    BulkObservableCollection<ScanRootsRowViewModel> Rows { get; }
    bool HasRows { get; }
    ScanRootsRowViewModel? SelectedRow { get; set; }

    ICommand SortByCommand { get; }
    ICommand ToggleExpandedCommand { get; }

    ScanRootsSortColumn SortColumn { get; }
    bool SortDescending { get; }

    string NameArrow { get; }
    string SizeArrow { get; }
    string ItemsArrow { get; }
    string FilesArrow { get; }
    string DupFilesArrow { get; }
    string DupBytesArrow { get; }
}
