using System;
using System.Threading.Tasks;

namespace DuplicateFileFinder.Util;

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