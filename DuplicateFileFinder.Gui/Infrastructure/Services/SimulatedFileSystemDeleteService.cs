namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public class SimulatedFileSystemDeleteService : IFileSystemDeleteService
{
    public async Task<(bool ok, string? error)> DeleteFileAsync(string fullPath)
    {
        await Task.Delay(5000);

        return (false, (string?)"Oops!");
    }

    // public async Task<(bool ok, string? error)> DeleteDirectoryAsync(string fullPath, bool recursive = true)
    // {
    //     await Task.Delay(5000);
    //
    //     return (false, (string?)"Oops!");
    // }

    public Task<(bool ok, string? error)> DeleteDirectoryAsync(string fullPath, bool recursive = true)
    {
        Thread.Sleep(5000);

        return Task.FromResult((false, (string?)"Oops!"));
    }
}
