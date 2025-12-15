using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class DirTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public DirTreeMapElement(
        DirRecord dir,
        ScanRoot scanRoot,
        DirAggregateStats dirAggregateStats,
        Func<string> relPathFactory, 
        double value)
    {
        ScanRoot = scanRoot;

        Name = dir.Name;
        Label = dir.Name;
        Value = value;

        RelativePathFactory = relPathFactory;

        Stats = dirAggregateStats;
    }

    public DirAggregateStats Stats { get; }
    
}
