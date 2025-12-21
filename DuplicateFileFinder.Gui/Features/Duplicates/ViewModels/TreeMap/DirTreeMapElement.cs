using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class DirTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public DirTreeMapElement(
        DirRecordV2 dir,
        ScanRoot scanRoot,
        DirAggregateStats dirAggregateStats,
        Func<string> relPathFactory, 
        double value,
        Func<string> nameResolver) : base(nameResolver)
    {
        ScanRoot = scanRoot;
        
        Label = dir.DirId.ToString();
        Value = value;

        RelativePathFactory = relPathFactory;

        Stats = dirAggregateStats;
    }

    public DirAggregateStats Stats { get; }
    
}
