using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class RepoCompactionPolicyTests
{
    [Fact]
    public void RepoCompactionPolicy_DefaultValues_AreStable()
    {
        var policy = new RepoCompactionPolicy();

        Assert.Equal(2.0, policy.RatioThreshold);
        Assert.Equal(16 * 1024 * 1024, policy.MinLogBytes);
        Assert.Equal(4, policy.MinDeltaCount);
    }
}