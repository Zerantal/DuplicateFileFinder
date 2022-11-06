using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using DuplicateFileFinder.ViewModels;
using TreeView = System.Windows.Controls.TreeView;

    namespace DuplicateFileFinder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        public DuplicateFilesViewModel DuplicateFiles { get; }

        public MainWindow()
        {
            DuplicateFiles = new DuplicateFilesViewModel();

            InitializeComponent();
        }

        private void FileSystemViewModel_AfterExplore(object sender, EventArgs e)
        {
            Cursor = System.Windows.Input.Cursors.Arrow;
        }

        private void FileSystemViewModel_BeforeExplore(object sender, EventArgs e)
        {
            Cursor = System.Windows.Input.Cursors.Wait;
        }

        private void AddSource_OnClick(object sender, RoutedEventArgs e)
        {
            var openFolderDialog = new FolderBrowserDialog();
            var result = openFolderDialog.ShowDialog();
            if (result.ToString() != string.Empty)
            {
                Console.WriteLine($@"folder {openFolderDialog.SelectedPath} added to sources");
                DuplicateFiles.SearchPaths.Add(openFolderDialog.SelectedPath);
            }
            
        }

        private void StartScan_OnClick(object sender, RoutedEventArgs e)
        {            
            Task.Run(() => DuplicateFiles.StartScan());
        }

        private void StopScan_OnClick(object sender, RoutedEventArgs e)
        {
            DuplicateFiles.StopScan();
        }

        private void TrvDuplicateFolders_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is TreeView view && !e.Handled)
            {
                e.Handled = true;
                var eventArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = MouseWheelEvent, Source = view
                };
                if (view.Parent is UIElement parent) parent.RaiseEvent(eventArgs);
            }
        }

        private void ScanLocation_OnClick(object sender, RoutedEventArgs e)
        {
            throw new NotImplementedException();
        }
    }
}
