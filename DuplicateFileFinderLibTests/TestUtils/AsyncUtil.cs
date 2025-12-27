using System;
using System.Threading.Tasks;

namespace DuplicateFileFinderLibTests.TestUtils;

public class AsyncUtil
{
    public static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not satisfied in time.");

            await Task.Delay(10);
        }
    }
}