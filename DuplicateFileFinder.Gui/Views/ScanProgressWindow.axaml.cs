using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace DuplicateFileFinder.Gui.Views;

public partial class ScanProgressWindow : Window
{
    public ScanProgressWindow()
    {
        InitializeComponent();
    }
    
    // protected override void OnOpened(EventArgs e)
    // {
    //     base.OnOpened(e);
    //
    //     PointerExited += (sender, args) =>
    //     {
    //         Console.WriteLine("Pointer exited dialog box: "  + args.Handled);
    //     };
    //     PointerMoved += (sender, args) =>
    //     {
    //         var p = args.GetPosition(this);
    //         Console.WriteLine($"Pointer moved dialog box: ({p.X}, {p.Y})");
    //     };
    //
    // }

}