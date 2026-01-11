using System.Collections.Generic;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Infrastructure.Services;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeFileSystemDeleteService : IFileSystemDeleteService
{
    public (bool ok, string? error) NextDeleteFileResult { get; set; } = (true, null);
    public (bool ok, string? error) NextDeleteDirectoryResult { get; set; } = (true, null);

    public List<(string Path, bool Recursive)> DeletedDirectories { get; } = [];
    public List<string> DeletedFiles { get; } = [];

    public Task<(bool ok, string? error)> DeleteFileAsync(string fullPath)
    {
        DeletedFiles.Add((fullPath));
        return Task.FromResult(NextDeleteFileResult);
    }

    public Task<(bool ok, string? error)> DeleteDirectoryAsync(string fullPath, bool recursive = true)
    {
        DeletedDirectories.Add((fullPath, recursive));
        return Task.FromResult(NextDeleteDirectoryResult);
    }
}
