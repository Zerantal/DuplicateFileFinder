using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace Deduplicator.Util;

/// <summary>
/// Class to retrieve and cache shell icons
/// </summary>
public class ShellIconManager
{
    private readonly Dictionary<IconKey, Icon> _iconCache;

    public ShellIconManager()
    {
        _iconCache = new Dictionary<IconKey, Icon>();
    }

    public Icon GetFolderIcon(IconReader.FolderType type, IconReader.IconSize size = IconReader.IconSize.Small)
    {
        var key = type == IconReader.FolderType.Open ? new IconKey("_openFolder", size) : new IconKey("_closedFolder", size);

        if (_iconCache.TryGetValue(key, out var icon))
            return icon;
        
        icon = IconReader.GetStockIcon(Shell32.ShStockIconId.SIID_FOLDER, size);
        _iconCache[key] = icon;
        return icon;
    }

    public Icon GetFileIcon(string path, IconReader.IconSize size = IconReader.IconSize.Small)
    {
        FileInfo fi = new FileInfo(path);
        var key = new IconKey(fi.Extension.ToUpper(), size);

        if (_iconCache.TryGetValue(key, out var icon))
            return icon;
            
        if (!File.Exists( path )) 
            throw new FileNotFoundException("File does not exist");

        icon = IconReader.GetFileIcon(path, size, false);
        _iconCache[key] = icon;
        return icon;
    }

    private class IconKey : IEqualityComparer<IconKey>
    {
        private readonly string _key;                   // extension or stock icon
        private readonly IconReader.IconSize _size;

        public IconKey(string key, IconReader.IconSize size)
        {
            _key = key;
            _size = size;
        }

        public bool Equals(IconKey x, IconKey y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(y, null)) return false;
            if (x.GetType() != y.GetType()) return false;
            return x._key == y._key && x._size == y._size;
        }

        public int GetHashCode(IconKey obj)
        {
            return HashCode.Combine(obj._key, (int) obj._size);
        }
    }
}