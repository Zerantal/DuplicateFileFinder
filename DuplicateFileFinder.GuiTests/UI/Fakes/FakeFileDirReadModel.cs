using System.Collections.Generic;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
// ReSharper disable UnassignedGetOnlyAutoProperty

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeFileDirReadModel : IFileDirReadModel
{
    public delegate bool TryGetDirPathByHandleDelegate(DirHandle handle, out string rel);
    public delegate bool TryGetFilePathByHandleDelegate(FileHandle handle, out string rel);
    public delegate bool TryGetFileDelegate(FileId fileId, out FileHandle handle);
    public delegate bool TryGetDirDelegate(DirId dirId, out DirHandle handle);
    public delegate bool TryGetDirPathByIdDelegate(DirId dirId, out string rel);
    public delegate bool TryGetFilePathByIdDelegate(FileId fileId, out string rel);

    public TryGetFileDelegate? TryGetFileImpl { get; set; }
    public TryGetDirDelegate? TryGetDirImpl { get; set; }
    public TryGetDirPathByHandleDelegate? TryGetDirPathByHandleImpl { get; set; }
    public TryGetFilePathByHandleDelegate? TryGetFilePathByHandleImpl { get; set; }
    public TryGetDirPathByIdDelegate? TryGetDirPathByIdImpl { get; set; }
    public TryGetFilePathByIdDelegate? TryGetFilePathByIdImpl { get; set; }

    public Dictionary<DirId, DirHandle> DirHandlesById { get; } = [];
    public Dictionary<FileId, FileHandle> FileHandlesById { get; } = [];
    public Dictionary<DirHandle, string> DirPathsByHandle { get; } = [];
    public Dictionary<FileHandle, string> FilePathsByHandle { get; } = [];
    public Dictionary<DirId, string> DirPathsById { get; } = [];
    public Dictionary<FileId, string> FilePathsById { get; } = [];

    public bool TryGetDirPathById(DirId dirId, out string relativePath)
    {
        if (TryGetDirPathByIdImpl is not null)
            return TryGetDirPathByIdImpl(dirId, out relativePath);

        return DirPathsById.TryGetValue(dirId, out relativePath!);
    }

    public bool TryGetDirPathByHandle(DirHandle handle, out string rel)
    {
        if (TryGetDirPathByHandleImpl is not null)
            return TryGetDirPathByHandleImpl(handle, out rel);

        return DirPathsByHandle.TryGetValue(handle, out rel!);
    }

    public bool TryGetDir(DirId dirId, out DirHandle handle)
    {
        if (TryGetDirImpl is not null)
            return TryGetDirImpl(dirId, out handle);

        return DirHandlesById.TryGetValue(dirId, out handle);
    }

    public bool TryGetFile(FileId fileId, out FileHandle handle)
    {
        if (TryGetFileImpl is not null)
            return TryGetFileImpl(fileId, out handle);

        return FileHandlesById.TryGetValue(fileId, out handle);
    }

    public int FileCount => FileHandlesById.Count;
    public int DirCount => DirHandlesById.Count;

    public bool TryGetFilePathById(FileId fileId, out string relativePath)
    {
        if (TryGetFilePathByIdImpl is not null)
            return TryGetFilePathByIdImpl(fileId, out relativePath);

        return FilePathsById.TryGetValue(fileId, out relativePath!);
    }

    public bool TryGetFilePathByHandle(FileHandle handle, out string rel)
    {
        if (TryGetFilePathByHandleImpl is not null)
            return TryGetFilePathByHandleImpl(handle, out rel);

        return FilePathsByHandle.TryGetValue(handle, out rel!);
    }

    public void Reset()
    {
        DirHandlesById.Clear();
        FileHandlesById.Clear();
        DirPathsByHandle.Clear();
        FilePathsByHandle.Clear();
        DirPathsById.Clear();
        FilePathsById.Clear();

        TryGetFileImpl = null;
        TryGetDirImpl = null;
        TryGetDirPathByHandleImpl = null;
        TryGetFilePathByHandleImpl = null;
        TryGetDirPathByIdImpl = null;
        TryGetFilePathByIdImpl = null;
    }
}
