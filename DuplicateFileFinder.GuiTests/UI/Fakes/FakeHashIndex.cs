using System;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeHashIndex : IHashIndexReadModel
{
    public Func<DuplicateQuery, int, int, DuplicateGroupPage>? GetGroupsPageImpl { get; set; }
    public GetGroupsPageWithFilterDelegate? GetGroupsPageWithFilterImpl { get; set; }
    public Func<HashGroupDescriptor, ReadOnlySpan<FileHandle>>? GetGroupFilesImpl { get; set; }

    public delegate DuplicateGroupPage GetGroupsPageWithFilterDelegate(
        DuplicateQuery query,
        SubtreeFilter filter,
        int offset,
        int count);

    public DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, int offset, int count) =>
        GetGroupsPageImpl?.Invoke(query, offset, count)
        ?? new DuplicateGroupPage(0, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

    public DuplicateGroupPage GetGroupsPage(
        in DuplicateQuery query,
        in SubtreeFilter filter,
        int offset,
        int count) =>
        GetGroupsPageWithFilterImpl?.Invoke(query, filter, offset, count)
        ?? new DuplicateGroupPage(0, 0, ReadOnlyMemory<HashGroupDescriptor>.Empty);

    public ReadOnlySpan<FileHandle> GetGroupFiles(in HashGroupDescriptor group) =>
        GetGroupFilesImpl is not null ? GetGroupFilesImpl.Invoke(group) : ReadOnlySpan<FileHandle>.Empty;

    public int TotalDuplicateFileCount { get; init; }
    public long TotalSpaceTakenByDuplicates { get; init; }
}
