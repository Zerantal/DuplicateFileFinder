using System.Diagnostics.CodeAnalysis;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class DirTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public DirTreeMapElement(
        ITreeMapDataResolver resolver,
        DirHandle dirHandle,
        ScanRoot scanRoot,
        double value) : base(resolver)
    {
        Dir = dirHandle;
        ScanRoot = scanRoot;
        Value = value;
    }

    public DirAggregateStats Stats => field ??= Resolver.GetDirStats(Dir);

    public DirHandle Dir { get; }

    // If I ever decide to show labels for big items.
    // public override string Label => Resolver.GetDirRecord(_dir).DirId.ToString();

    protected override string ResolveName()
        => Resolver.DecodeDirName(Dir);

    protected override string ResolveRelativePath()
    {
        DirRecordV2 rec;
        try
        { rec = Resolver.GetDirRecord(Dir); }
        catch { return string.Empty; }

        return Resolver.GetRelativePath(rec.DirId);
    }
}
