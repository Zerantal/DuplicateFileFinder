using DuplicateFileFinder.Util;

namespace DuplicateFileFinder.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
// ReSharper disable once UnusedMember.Global
public partial class MainWindow
{
    public static readonly ShellIconManager IconManager = new();

    #region Constructors
    public MainWindow()
    {
        InitializeComponent();
    }
    #endregion
}