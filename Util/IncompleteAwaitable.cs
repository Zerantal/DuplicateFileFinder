using System.Runtime.CompilerServices;

namespace Util;

/// <summary>
/// Represents an awaitable who's awaiter has not yet completed.
/// </summary>
/// <typeparam name="TResult">The result type.</typeparam>
public class IncompleteAwaitable<TResult>
{
    private readonly TaskAwaiter<TResult> _taskAwaiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncompleteAwaitable{T}"/> class.
    /// </summary>
    /// <param name="task">The <see cref="Task{TResult}"/> wrapped by this <see cref="IncompleteAwaitable{T}"/>.</param>
    public IncompleteAwaitable(Task<TResult> task)
    {
        _taskAwaiter = task.GetAwaiter();
    }

    ///// <summary>
    ///// Gets an awaiter used to await this <see cref="Task{TResult}"/>
    ///// </summary>
    ///// <returns></returns>
    public IncompleteAwaiter<TResult> GetAwaiter()
    {
        return new IncompleteAwaiter<TResult>(_taskAwaiter);
    }
}

/// <summary>
/// An awaiter that has never completed regardless of the 
/// state of the underlying <see cref="TaskAwaiter{TResult}"/>/>
/// </summary>
/// <typeparam name="TResult">The result type.</typeparam>
public class IncompleteAwaiter<TResult> : INotifyCompletion
{
    private readonly TaskAwaiter<TResult> _taskAwaiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncompleteAwaiter{TResult}"/> class.
    /// </summary>
    /// <param name="taskAwaiter">The underlying <see cref="TaskAwaiter{TResult}"/></param>
    public IncompleteAwaiter(TaskAwaiter<TResult> taskAwaiter)
    {
        this._taskAwaiter = taskAwaiter;
    }

    /// <summary>
    /// Attaches a continuation to the underlying <see cref="TaskAwaiter{TResult}"/>. 
    /// </summary>
    /// <param name="continuation">The continuation delegate to be attached.</param>
    public void OnCompleted(Action continuation)
    {
        _taskAwaiter.OnCompleted(continuation);
    }

    /// <summary>
    /// Gets a value indicating whether this awaiter is completed.
    /// </summary>
    /// <remarks>
    /// This property will always return false to ensure continuation.
    /// </remarks>
    public bool IsCompleted => false;

    /// <summary>
    /// Gets the result from the underlying <see cref="TaskAwaiter{TResult}"/>.
    /// </summary>
    /// <returns>The completed <typeparamref name="TResult"/> value.</returns>
    public TResult GetResult()
    {
        return _taskAwaiter.GetResult();
    }
}