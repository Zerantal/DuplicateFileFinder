using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class FileTreeMapElement : RepoTreeMapElement
{
    private readonly FileHandle _file;

    [SetsRequiredMembers]
    public FileTreeMapElement(
        ITreeMapDataResolver resolver,
        FileHandle fileHandle,
        ScanRoot scanRoot,
        double value) : base(resolver)
    {
        _file = fileHandle;
        ScanRoot = scanRoot;
        Value = value;
    }
    
    public long SizeBytes => (long)Value;

    // If I ever decide to show labels for big items.
    // public override string Label => Resolver.GetFileRecord(_file).FileId.ToString();

    protected override string ResolveName()
        => Resolver.DecodeFileName(_file);

    protected override string ResolveRelativePath()
    {
        // Derived lazily: handle -> record -> dirId -> relpath.
        FileRecordV2 rec;
        try { rec = Resolver.GetFileRecord(_file); }
        catch { return string.Empty; }

        return Resolver.GetRelativePath(rec.DirId);
    }
}