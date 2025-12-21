using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DuplicateFileFinderLibTests.TestUtils;

public class MethodCounter
{
    public readonly Dictionary<string, int> MethodCallCounts = new();

    public int GetMethodCallCount(string methodName) => MethodCallCounts.GetValueOrDefault(methodName);
    
    public void IncrementMethodCalCount([CallerMemberName] string memberName = "")
    {
            MethodCallCounts[memberName] = MethodCallCounts.GetValueOrDefault(memberName) + 1;
    }
}