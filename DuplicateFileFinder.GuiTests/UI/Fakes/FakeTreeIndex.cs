using System;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeTreeIndex : ITreeIndexReadModel
{
    public Func<DirHandle, DirAggregateStats>? GetDirStatsImpl { get; set; }
    public Func<DirHandle, ReadOnlySpan<DirHandle>>? GetChildDirsImpl { get; set; }
    public Func<DirHandle, ReadOnlySpan<FileHandle>>? GetChildFilesImpl { get; set; }
    public TryGetSubtreeRangeDelegate? TryGetSubtreeRangeImpl { get; set; }
    public TryGetFileDirPreorderDelegate? TryGetFileDirPreorderImpl { get; set; }

    public delegate bool TryGetSubtreeRangeDelegate(DirHandle dir, out SubtreeRange range);
    public delegate bool TryGetFileDirPreorderDelegate(FileHandle file, out int preorder);

    public DirAggregateStats GetDirStats(DirHandle dirId) =>
        GetDirStatsImpl?.Invoke(dirId) ?? new DirAggregateStats
        {
            TotalBytes = 0,
            FileCount = 0,
            DirCount = 0,
            DuplicateFiles = 0,
            DuplicateBytes = 0
        };

    public ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir) =>
        GetChildDirsImpl is not null ? GetChildDirsImpl.Invoke(dir) : ReadOnlySpan<DirHandle>.Empty;

    public ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir) =>
        GetChildFilesImpl is not null ? GetChildFilesImpl.Invoke(dir) : ReadOnlySpan<FileHandle>.Empty;

    public bool TryGetSubtreeRange(DirHandle dir, out SubtreeRange range)
    {
        if (TryGetSubtreeRangeImpl is not null)
            return TryGetSubtreeRangeImpl(dir, out range);

        range = default;
        return false;
    }

    public bool TryGetFileDirPreorder(FileHandle file, out int preorder)
    {
        if (TryGetFileDirPreorderImpl is not null)
            return TryGetFileDirPreorderImpl(file, out preorder);

        preorder = 0;
        return false;
    }

    public void Reset()
    {
        GetDirStatsImpl = null;
        GetChildDirsImpl = null;
        GetChildFilesImpl = null;
        TryGetSubtreeRangeImpl = null;
        TryGetFileDirPreorderImpl = null;
    }
}
