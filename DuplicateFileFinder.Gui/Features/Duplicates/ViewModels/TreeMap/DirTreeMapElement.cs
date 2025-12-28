using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class DirTreeMapElement : RepoTreeMapElement
{
    private readonly DirHandle _dir;

    [SetsRequiredMembers]
    public DirTreeMapElement(
        ITreeMapDataResolver resolver,
        DirHandle dirHandle,
        ScanRoot scanRoot,
        double value) : base(resolver)
    {
        _dir = dirHandle;
        ScanRoot = scanRoot;
        Value = value;
    }

    public DirAggregateStats Stats => field ??= Resolver.GetDirStats(_dir);

    // If I ever decide to show labels for big items.
    // public override string Label => Resolver.GetDirRecord(_dir).DirId.ToString();
    
    protected override string ResolveName()
        => Resolver.DecodeDirName(_dir);

    protected override string ResolveRelativePath()
    {
        DirRecordV2 rec;
        try { rec = Resolver.GetDirRecord(_dir); }
        catch { return string.Empty; }

        return Resolver.GetRelativePath(rec.DirId);
    }
}
