using DuplicateFileFinderLib.Repository.Core.Models;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class DirHandleTests
{
    [Fact]
    public void Default_IsInvalid()
    {
        var h = DirHandle.Invalid;

        Assert.False(h.IsValid);
        Assert.Equal(-1, h.ScanRootId);
        Assert.Equal(-1, h.Index);
    }

    [Fact]
    public void ZeroIndex_IsValid()
    {
        var h = new DirHandle(ScanRootId: 0, Index: 0);

        Assert.True(h.IsValid);
    }

    [Fact]
    public void NegativeScanRoot_IsInvalid()
    {
        var h = new DirHandle(ScanRootId: -1, Index: 0);

        Assert.False(h.IsValid);
    }

    [Fact]
    public void NegativeIndex_IsInvalid()
    {
        var h = new DirHandle(ScanRootId: 0, Index: -1);

        Assert.False(h.IsValid);
    }
}
