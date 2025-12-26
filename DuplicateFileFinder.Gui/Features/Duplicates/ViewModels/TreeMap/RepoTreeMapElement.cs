using Avalonia.Controls;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public abstract class RepoTreeMapElement(Func<string> nameResolver) : ITreeMapNodeElement
{
    private Func<string> NameResolver { get; } = nameResolver;

    // Common data for dir/file
    public required Func<string> RelativePathFactory { get; init; }

    public string Name
    {
        get
        {
            field ??= SafeInvoke(NameResolver);

            return field;
        }
        
    } = null;

    public required string Label { get; init; }

    public Func<Control> ToolTipFactory => () => new ContentControl
    {
        Content = this
    };
    
    public required double Value { get; init; }

    protected ScanRoot? ScanRoot { get; init; }

    // Bindable convenience
    public string VolumeLabel => ScanRoot?.VolumeLabel ?? "(unknown)";
    public string RelativePath => SafeInvoke(RelativePathFactory);

    private static string SafeInvoke(Func<string> f)
    {
        try { return f(); }
        catch { return string.Empty; }
    }
}
    