namespace DuplicateFileFinderLib.Util;

public static class IEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> enumerable)
    {
        foreach (var item in enumerable)
        {
            yield return Task.FromResult(item).Result;
        }
    }   
}