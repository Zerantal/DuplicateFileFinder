using Avalonia.Controls;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public abstract class RepoTreeMapElement : ITreeMapNodeElement
{
    private readonly Lock _toolTipLock = new();

    private Func<Control>? _toolTipFactory;

    // Common data for dir/file
    protected string? VolumeLabel => ScanRoot?.VolumeLabel;
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    protected ScanRoot? ScanRoot { get; init; }

    // ITreeMapNodeElement interface
    public double Value { get; init; }

    public Control CreateToolTip()
    {
        var f = _toolTipFactory;
        if (f != null)
            return f();

        lock (_toolTipLock)
        {
            f = _toolTipFactory;
            if (f != null)
                return f();

            // Build factory ONCE, not the control
            _toolTipFactory = BuildToolTipFactory();
            return _toolTipFactory();
        }
    }

    public required string Label { get; init; }
    
    protected abstract Func<Control> BuildToolTipFactory();
}