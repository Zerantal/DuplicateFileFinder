using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed class FileTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public FileTreeMapElement(
        FileRecord file,
        ScanRoot scanRoot,
        Func<string> relPathFactory)
    {
        ScanRoot = scanRoot;

        Name = file.Name;
        Label = file.Name;
        Value = file.Size;
        
        RelativePathFactory = relPathFactory;
    }
    
    public long SizeBytes => (long)Value;
}