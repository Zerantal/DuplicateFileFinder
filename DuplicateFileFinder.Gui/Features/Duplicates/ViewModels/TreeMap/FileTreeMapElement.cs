using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class FileTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public FileTreeMapElement(
        FileRecordV2 file,
        ScanRoot scanRoot,
        Func<string> relPathFactory,
        Func<string> nameResolver) : base(nameResolver)
    {
        ScanRoot = scanRoot;
        
        Label = file.FileId.ToString();
        Value = file.Size;
        
        RelativePathFactory = relPathFactory;
    }
    
    public long SizeBytes => (long)Value;
}