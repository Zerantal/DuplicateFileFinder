namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public sealed class FileSystemDeleteService : IFileSystemDeleteService
{
    public Task<(bool ok, string? error)> DeleteFileAsync(string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return Task.FromResult<(bool, string?)>((false, "Path was empty."));

            var p = Path.GetFullPath(fullPath);

            if (!File.Exists(p))
                return Task.FromResult<(bool, string?)>((false, "File does not exist."));

            TryClearReadOnly(p);

            File.Delete(p);

            return Task.FromResult<(bool, string?)>((true, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(bool, string?)>((false, ex.Message));
        }
    }

    public Task<(bool ok, string? error)> DeleteDirectoryAsync(string fullPath, bool recursive = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return Task.FromResult<(bool, string?)>((false, "Path was empty."));

            var p = Path.GetFullPath(fullPath);

            if (!Directory.Exists(p))
                return Task.FromResult<(bool, string?)>((false, "Directory does not exist."));

            // Safety: refuse to delete volume root
            var root = Path.GetPathRoot(p);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(NormalizeDir(p), NormalizeDir(root), StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<(bool, string?)>((false, "Refusing to delete a volume root directory."));
            }

            if (recursive)
                ClearReadOnlyRecursively(p);

            Directory.Delete(p, recursive);

            return Task.FromResult<(bool, string?)>((true, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(bool, string?)>((false, ex.Message));
        }
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // ignore - best effort
        }
    }

    private static void ClearReadOnlyRecursively(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                TryClearReadOnly(file);

            foreach (var dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                TryClearReadOnly(dir);

            TryClearReadOnly(directory);
        }
        catch
        {
            // ignore - best effort
        }
    }

    private static string NormalizeDir(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
