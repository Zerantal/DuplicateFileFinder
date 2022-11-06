using System;
using System.Drawing;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Deduplicator.Util;

internal class FileManager
{
    public static ImageSource GetImageSource(string filename)
    {
        return GetImageSource(filename, new Size(16, 16));
    }

    public static ImageSource GetImageSource(string filename, Size size)
    {
        using var icon = ShellManager.GetIcon(Path.GetExtension(filename), ItemType.File, IconSize.Small, ItemState.Undefined);

        return Imaging.CreateBitmapSourceFromHIcon(icon.Handle,
            System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(size.Width, size.Height));
    }

    private static readonly string[] SizeSuffixes = {"bytes", "KB", "MB", "GB", "TB", "PB"};

    public static string SizeSuffix(long value, int decimalPlaces = 1)
    {
        if (decimalPlaces < 0) { throw new ArgumentOutOfRangeException(nameof(decimalPlaces)); }

        var strFormat = "{0:n" + decimalPlaces + "}";
        switch (value)
        {
            case < 0:
                return "-" + SizeSuffix(-value, decimalPlaces);
            case 0:
                return string.Format(strFormat + " bytes", 0);
        }

        // mag is 0 for bytes, 1 for KB, 2, for MB, etc.
        int mag = (int)Math.Log(value, 1024);

        // 1L << (mag * 10) == 2 ^ (10 * mag) 
        // [i.e. the number of bytes in the unit corresponding to mag]
        decimal adjustedSize = (decimal)value / (1L << (mag * 10));

        // make adjustment when the value is large enough that
        // it would round up to 1000 or more
        // ReSharper disable once InvertIf
        if (Math.Round(adjustedSize, decimalPlaces) >= 1000)
        {
            mag += 1;
            adjustedSize /= 1024;
        }

        return string.Format(strFormat + " {1}", 
            adjustedSize, 
            SizeSuffixes[mag]);
    }
}