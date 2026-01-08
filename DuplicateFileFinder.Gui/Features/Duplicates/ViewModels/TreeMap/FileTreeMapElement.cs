using System.Diagnostics.CodeAnalysis;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class FileTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public FileTreeMapElement(
        ITreeMapDataResolver resolver,
        FileHandle fileHandle,
        ScanRoot scanRoot,
        double value) : base(resolver)
    {
        File = fileHandle;
        ScanRoot = scanRoot;
        Value = value;
    }

    public long SizeBytes => (long)Value;

    public FileHandle File { get; }

    // If I ever decide to show labels for big items.
    // public override string Label => Resolver.GetFileRecord(_file).FileId.ToString();

    protected override string ResolveName()
        => Resolver.DecodeFileName(File);

    protected override string ResolveRelativePath()
    {
        // Derived lazily: handle -> record -> dirId -> relpath.
        FileRecordV2 rec;
        try
        { rec = Resolver.GetFileRecord(File); }
        catch { return string.Empty; }

        return Resolver.GetRelativePath(rec.DirId);
    }
}
