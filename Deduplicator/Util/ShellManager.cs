using System;
using System.Drawing;
using System.Runtime.InteropServices;
using DuplicateFileFinder.Enums;
using DuplicateFileFinder.Structs;

namespace DuplicateFileFinder.Util
{
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
                throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error());

            try
            {
                return (Icon) Icon.FromHandle(fileInfo.hIcon).Clone();
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
}
