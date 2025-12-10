using System;

namespace DuplicateFileFinderLibTests.TestUtils.Fakes;

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start;
}