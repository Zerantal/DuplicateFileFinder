// ReSharper disable InconsistentNaming
namespace Bench;

public static class LongExtensions
{
    public static string ToSizeString(this long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        const long TB = GB * 1024;

        if (bytes < KB)
            return $"{bytes} B";

        if (bytes < MB)
            return $"{bytes / (double)KB:0.##} KB";

        if (bytes < GB)
            return $"{bytes / (double)MB:0.##} MB";

        if (bytes < TB)
            return $"{bytes / (double)GB:0.##} GB";

        return $"{bytes / (double)TB:0.##} TB";
    }
}
