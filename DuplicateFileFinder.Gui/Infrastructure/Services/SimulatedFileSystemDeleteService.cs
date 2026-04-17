namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public class SimulatedFileSystemDeleteService : IFileSystemDeleteService
{
    public async Task<(bool ok, string? error)> DeleteFileAsync(string fullPath)
    {
        await Task.Delay(2000);

        return (true, (string?)"Oops!");
    }

    public async Task<(bool ok, string? error)> DeleteDirectoryAsync(string fullPath, bool recursive = true)
    {
        await Task.Delay(2000);

        return (true, (string?)"Oops!");
    }
}
