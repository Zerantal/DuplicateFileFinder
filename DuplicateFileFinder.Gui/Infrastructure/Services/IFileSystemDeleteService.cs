namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public interface IFileSystemDeleteService
{
    Task<(bool ok, string? error)> DeleteFileAsync(string fullPath);
    Task<(bool ok, string? error)> DeleteDirectoryAsync(string fullPath, bool recursive = true);
}
