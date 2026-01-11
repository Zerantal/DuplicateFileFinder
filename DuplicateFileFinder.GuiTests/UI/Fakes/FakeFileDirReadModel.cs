using System;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
// ReSharper disable UnassignedGetOnlyAutoProperty

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeFileDirReadModel : IFileDirReadModel
{
    public delegate bool TryGetDirPathByHandleDelegate(DirHandle handle, out string rel);
    public delegate bool TryGetFilePathByHandleDelegate(FileHandle handle, out string rel);
    public delegate bool TryGetFileDelegate(long fileId, out FileHandle handle);

    public TryGetFileDelegate? TryGetFileImpl { get; set; }
    public TryGetDirPathByHandleDelegate? TryGetDirPathByHandleImpl { get; init; }
    public TryGetFilePathByHandleDelegate? TryGetFilePathByHandleImpl { get; init; }

    public bool TryGetDirPathById(long dirId, out string relativePath) => throw new NotImplementedException();

    public bool TryGetDirPathByHandle(DirHandle handle, out string rel)
    {
        if (TryGetDirPathByHandleImpl is not null)
            return TryGetDirPathByHandleImpl(handle, out rel);

        rel = "";
        return false;
    }

    public bool TryGetDir(long dirId, out DirHandle handle) => throw new NotImplementedException();

    public bool TryGetFile(long fileId, out FileHandle handle)
    {
        if (TryGetFileImpl is not null)
            return TryGetFileImpl(fileId, out handle);

        handle = default;
        return false;
    }

    public int FileCount { get; }
    public int DirCount { get; }

    public bool TryGetFilePathById(long fileId, out string relativePath) => throw new NotImplementedException();

    public bool TryGetFilePathByHandle(FileHandle handle, out string rel)
    {
        if (TryGetFilePathByHandleImpl is not null)
            return TryGetFilePathByHandleImpl(handle, out rel);

        rel = "";
        return false;
    }
}
