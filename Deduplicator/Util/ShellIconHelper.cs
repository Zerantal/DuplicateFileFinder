using System;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.InteropServices;

// ReSharper disable UnusedMember.Global
// ReSharper disable IdentifierTypo
// ReSharper disable InconsistentNaming
// ReSharper disable CommentTypo

namespace Deduplicator.Util;

/// <summary>
/// Provides static methods to read system icons for both folders and files.
/// </summary>
/// <example>
/// <code>IconReader.GetFileIcon("c:\\general.xls");</code>
/// </example>
public class IconReader
{

    /// <summary>
    /// Options to specify the size of icons to return.
    /// </summary>
    public enum IconSize : uint
    {
        /// <summary>
        /// Specify large icon - 32 pixels by 32 pixels.
        /// </summary>
        Large = 0x0,
        /// <summary>
        /// Specify small icon - 16 pixels by 16 pixels.
        /// </summary>
        Small = 0x1
    }

    /// <summary>
    /// Options to specify whether folders should be in the open or closed state.
    /// </summary>
    public enum FolderType
    {
        /// <summary>
        /// Specify open folder.
        /// </summary>
        Open = 0,
        /// <summary>
        /// Specify closed folder.
        /// </summary>
        Closed = 1
    }

    /// <summary>
    /// Returns an icon for a given file - indicated by the name parameter.
    /// </summary>
    /// <param name="name">Pathname for file.</param>
    /// <param name="size">Large or small</param>
    /// <param name="linkOverlay">Whether to include the link icon</param>
    /// <returns>System.Drawing.Icon</returns>
    public static Icon GetFileIcon(string name, IconSize size, bool linkOverlay)
    {
        Shell32.ShFileInfo ShFi = new Shell32.ShFileInfo();
        uint flags = Shell32.ShgfiIcon | Shell32.ShgfiUsefileattributes;

        if (linkOverlay) flags += Shell32.ShgfiLinkoverlay;

        /* Check the size specified for return. */
        if (IconSize.Small == size)
        {
            flags += Shell32.ShgfiSmallicon;
        }
        else
        {
            flags += Shell32.ShgfiLargeicon;
        }

        Shell32.SHGetFileInfo(name,
            Shell32.FileAttributeNormal,
            ref ShFi,
            (uint)Marshal.SizeOf(ShFi),
            flags);

        // Copy (clone) the returned icon to a new object, thus allowing us to clean-up properly
        Icon icon = (Icon)Icon.FromHandle(ShFi.hIcon).Clone();
        User32.DestroyIcon(ShFi.hIcon);     // Cleanup
        return icon;
    }

    public static Icon GetStockIcon(Shell32.ShStockIconId iconId, IconSize size)
    {
        // Need to add size check, although errors generated at present!
        uint flags = Shell32.ShgfiIcon | (uint)size;

        var info = new Shell32.ShStockIconInfo();
        info.cbSize = (uint) Marshal.SizeOf(info);

        // ReSharper disable once UnusedVariable
        var error = Shell32.SHGetStockIconInfo((uint)iconId, flags, ref info);

        // TODO: if error return "error" icon

        // Now clone the icon, so that it can be successfully stored in an ImageList
        var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();

        User32.DestroyIcon(info.hIcon);     // Cleanup
        return icon;
    }
}

/// <summary>
/// Wraps necessary Shell32.dll structures and functions required to retrieve Icon Handles using SHGetFileInfo. Code
/// courtesy of MSDN Cold Rooster Consulting case study.
/// </summary>
/// 

// This code has been left largely untouched from that in the CRC example. The main changes have been moving
// the icon reading code over to the IconReader type.
public class Shell32
{

    public const int MaxPath = 260;
    [StructLayout(LayoutKind.Sequential)]
    public struct ShItemId
    {
        public ushort cb;
        [MarshalAs(UnmanagedType.LPArray)]
        public byte[] abID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ItemIdList
    {
        public ShItemId mkId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pIdlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpFn;
        public int lParam;
        public IntPtr iImage;
    }

    // Browsing for directory.
    public const uint BifReturnOnlyFsDirs = 0x0001;
    public const uint BifDontGoBelowDomain = 0x0002;
    public const uint BifStatusText = 0x0004;
    public const uint BifReturnFsAncestors = 0x0008;
    public const uint BifEditBox = 0x0010;
    public const uint BifValidate = 0x0020;
    public const uint BifNewDialogStyle = 0x0040;
    public const uint BifUseNewUi = BifNewDialogStyle | BifEditBox;
    public const uint BifBrowseIncludeUrls = 0x0080;
    public const uint BifBrowseForComputer = 0x1000;
    public const uint BifBrowseForPrinter = 0x2000;
    public const uint BifBrowseIncludeFiles = 0x4000;
    public const uint BifShareable = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct ShFileInfo
    {
        public const int NameSize = 80;
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NameSize)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShStockIconInfo
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysIconIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]    // wchar
        public string szPath;
    }

    // ReSharper disable once InconsistentNaming
    [SuppressMessage("ReSharper", "IdentifierTypo")]
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public enum ShStockIconId
    {
        SIID_DOCNOASSOC = 0,
        SIID_DOCASSOC = 1,
        SIID_APPLICATION = 2,
        SIID_FOLDER = 3,
        SIID_FOLDEROPEN = 4,
        SIID_DRIVE525 = 5,
        SIID_DRIVE35 = 6,
        SIID_DRIVEREMOVE = 7,
        SIID_DRIVEFIXED = 8,
        SIID_DRIVENET = 9,
        SIID_DRIVENETDISABLED = 10,
        SIID_DRIVECD = 11,
        SIID_DRIVERAM = 12,
        SIID_WORLD = 13,
        SIID_SERVER = 15,
        SIID_PRINTER = 16,
        SIID_MYNETWORK = 17,
        SIID_FIND = 22,
        SIID_HELP = 23,
        SIID_SHARE = 28,
        SIID_LINK = 29,
        SIID_SLOWFILE = 30,
        SIID_RECYCLER = 31,
        SIID_RECYCLERFULL = 32,
        SIID_MEDIACDAUDIO = 40,
        SIID_LOCK = 47,
        SIID_AUTOLIST = 49,
        SIID_PRINTERNET = 50,
        SIID_SERVERSHARE = 51,
        SIID_PRINTERFAX = 52,
        SIID_PRINTERFAXNET = 53,
        SIID_PRINTERFILE = 54,
        SIID_STACK = 55,
        SIID_MEDIASVCD = 56,
        SIID_STUFFEDFOLDER = 57,
        SIID_DRIVEUNKNOWN = 58,
        SIID_DRIVEDVD = 59,
        SIID_MEDIADVD = 60,
        SIID_MEDIADVDRAM = 61,
        SIID_MEDIADVDRW = 62,
        SIID_MEDIADVDR = 63,
        SIID_MEDIADVDROM = 64,
        SIID_MEDIACDAUDIOPLUS = 65,
        SIID_MEDIACDRW = 66,
        SIID_MEDIACDR = 67,
        SIID_MEDIACDBURN = 68,
        SIID_MEDIABLANKCD = 69,
        SIID_MEDIACDROM = 70,
        SIID_AUDIOFILES = 71,
        SIID_IMAGEFILES = 72,
        SIID_VIDEOFILES = 73,
        SIID_MIXEDFILES = 74,
        SIID_FOLDERBACK = 75,
        SIID_FOLDERFRONT = 76,
        SIID_SHIELD = 77,
        SIID_WARNING = 78,
        SIID_INFO = 79,
        SIID_ERROR = 80,
        SIID_KEY = 81,
        SIID_SOFTWARE = 82,
        SIID_RENAME = 83,
        SIID_DELETE = 84,
        SIID_MEDIAAUDIODVD = 85,
        SIID_MEDIAMOVIEDVD = 86,
        SIID_MEDIAENHANCEDCD = 87,
        SIID_MEDIAENHANCEDDVD = 88,
        SIID_MEDIAHDDVD = 89,
        SIID_MEDIABLURAY = 90,
        SIID_MEDIAVCD = 91,
        SIID_MEDIADVDPLUSR = 92,
        SIID_MEDIADVDPLUSRW = 93,
        SIID_DESKTOPPC = 94,
        SIID_MOBILEPC = 95,
        SIID_USERS = 96,
        SIID_MEDIASMARTMEDIA = 97,
        SIID_MEDIACOMPACTFLASH = 98,
        SIID_DEVICECELLPHONE = 99,
        SIID_DEVICECAMERA = 100,
        SIID_DEVICEVIDEOCAMERA = 101,
        SIID_DEVICEAUDIOPLAYER = 102,
        SIID_NETWORKCONNECT = 103,
        SIID_INTERNET = 104,
        SIID_ZIPFILE = 105,
        SIID_SETTINGS = 106,
        SIID_DRIVEHDDVD = 132,
        SIID_DRIVEBD = 133,
        SIID_MEDIAHDDVDROM = 134,
        SIID_MEDIAHDDVDR = 135,
        SIID_MEDIAHDDVDRAM = 136,
        SIID_MEDIABDROM = 137,
        SIID_MEDIABDR = 138,
        SIID_MEDIABDRE = 139,
        SIID_CLUSTEREDDRIVE = 140,
        SIID_MAX_ICONS = 181
    }

    public const uint ShgfiIcon = 0x000000100;     // get icon
    public const uint ShgfiDisplayname = 0x000000200;     // get display name
    public const uint ShgfiTypename = 0x000000400;     // get type name
    public const uint ShgfiAttributes = 0x000000800;     // get attributes
    public const uint ShgfiIconlocation = 0x000001000;     // get icon location
    public const uint ShgfiExetype = 0x000002000;     // return exe type
    public const uint ShgfiSysiconindex = 0x000004000;     // get system icon index
    public const uint ShgfiLinkoverlay = 0x000008000;     // put a link overlay on icon
    public const uint ShgfiSelected = 0x000010000;     // show icon in selected state
    public const uint ShgfiAttrSpecified = 0x000020000;     // get only specified attributes
    public const uint ShgfiLargeicon = 0x000000000;     // get large icon
    public const uint ShgfiSmallicon = 0x000000001;     // get small icon
    public const uint ShgfiOpenicon = 0x000000002;     // get open icon
    public const uint ShgfiShelliconsize = 0x000000004;     // get shell size icon
    public const uint ShgfiPidl = 0x000000008;     // pszPath is a pidl
    public const uint ShgfiUsefileattributes = 0x000000010;     // use passed dwFileAttribute
    public const uint ShgfiAddoverlays = 0x000000020;     // apply the appropriate overlays
    public const uint ShgfiOverlayindex = 0x000000040;     // Get the index of the overlay

    public const uint FileAttributeDirectory = 0x00000010;
    public const uint FileAttributeNormal = 0x00000080;

    [Flags]
    public enum SHGSI : uint
    {
        SHGSI_ICONLOCATION = 0,
        SHGSI_ICON = 0x000000100,
        SHGSI_SYSICONINDEX = 0x000004000,
        SHGSI_LINKOVERLAY = 0x000008000,
        SHGSI_SELECTED = 0x000010000,
        SHGSI_LARGEICON = 0x000000000,
        SHGSI_SMALLICON = 0x000000001,
        SHGSI_SHELLICONSIZE = 0x000000004
    }

    [DllImport("Shell32.dll")]
    public static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags
    );

    [DllImport("shell32.dll")]
    public static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref ShStockIconInfo psii);
}

/// <summary>
/// Wraps necessary functions imported from User32.dll. Code courtesy of MSDN Cold Rooster Consulting example.
/// </summary>
public class User32
{
    /// <summary>
    /// Provides access to function required to delete handle. This method is used internally
    /// and is not required to be called separately.
    /// </summary>
    /// <param name="hIcon">Pointer to icon handle.</param>
    /// <returns>N/A</returns>
    [DllImport("User32.dll")]
    public static extern int DestroyIcon(IntPtr hIcon);
}