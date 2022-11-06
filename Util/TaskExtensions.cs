namespace Util;

/// <summary>
/// Extends the <see cref="Task{TResult}"/> class.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Creates an "incomplete" <see cref="Task{TResult}"/> from 
    /// the given <paramref name="task"/>. 
    /// </summary>
    /// <typeparam name="TResult">The result type returned from the task.</typeparam>
    /// <param name="task">The target <see cref="Task{TResult}"/>.</param>
    /// <returns>A new <see cref="Task{TResult}"/> where the <see cref="Task.IsCompleted"/> is set to false.</returns>
    public static Task<TResult> GetIncompleteTask<TResult>(this Task<TResult> task)
    {
        var incompleteAwaitable = new IncompleteAwaitable<TResult>(task);
        return Task.Run(async () => await incompleteAwaitable);
    }
}