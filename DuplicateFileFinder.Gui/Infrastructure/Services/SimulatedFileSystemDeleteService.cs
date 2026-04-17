namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public class SimulatedFileSystemDeleteService : IFileSystemDeleteService
{
    public async Task<(bool ok, string? error)> DeleteFileAsync(string fullPath)
    {
        await Task.Delay(5000);

        return (true, (string?)"Delete succeeded!");
    }



    public Task<(bool ok, string? error)> DeleteDirectoryAsync(string fullPath, bool recursive = true)
    {
        try
        {
            // Thread.Sleep(5000);

            return Task.FromResult<(bool ok, string? error)>((true, null));
        }
        catch (Exception exception)
        {
            return Task.FromException<(bool ok, string? error)>(exception);
        }
    }
}
