using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Deduplicator.Util;

internal class ShellManager
{
    public static Icon GetIcon(string path, ItemType type, IconSize iconSize, ItemState state)
    {
        var attributes = (uint) (type == ItemType.Folder ? FileAttribute.Directory : FileAttribute.File);
        var flags = (uint) (ShellAttribute.Icon | ShellAttribute.UseFileAttributes);

        if (type == ItemType.Folder && state == ItemState.Open)
        {
            flags |= (uint) ShellAttribute.OpenIcon;
        }

        flags |= iconSize == IconSize.Small ? (uint) ShellAttribute.SmallIcon : (uint) ShellAttribute.LargeIcon;

        var fileInfo = new ShellFileInfo();
        var size = (uint) Marshal.SizeOf(fileInfo);
        var result = Interop.SHGetFileInfo(path, attributes, out fileInfo, size, flags);

        if (result == IntPtr.Zero)
            throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())!;

        try
        {
#pragma warning disable CA1416 // Validate platform compatibility
            return (Icon) Icon.FromHandle(fileInfo.hIcon).Clone();
#pragma warning restore CA1416 // Validate platform compatibility
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            Interop.DestroyIcon(fileInfo.hIcon);
        }
    }
}