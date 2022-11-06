using System;
using System.Threading.Tasks;

namespace Deduplicator.Util;

public static class TaskUtilities
{
    public static async void FireAndForgetSafeAsync(this Task task, IErrorHandler handler = null)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            handler?.HandleError(e);
        }
    }
}