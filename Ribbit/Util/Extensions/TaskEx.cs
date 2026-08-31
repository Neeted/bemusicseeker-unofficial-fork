using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Ribbit.Util.Extensions;

public static class TaskEx
{
    /// <summary>
    /// Observes a task for diagnostics while preserving its completion outcome for the caller.
    /// </summary>
    /// <param name="task">The task whose outcome must remain observable.</param>
    /// <param name="logger">The diagnostic callback invoked after the task settles.</param>
    /// <param name="memberName">The originating member name.</param>
    /// <param name="filePath">The originating source path.</param>
    /// <param name="lineNumber">The originating source line.</param>
    /// <returns>A task with the same success, fault, or cancellation outcome as <paramref name="task"/>.</returns>
    public static async Task LoggingAndPropagate(
        this Task task,
        Action<Task, string, string, int> logger,
        [CallerMemberName] string memberName = null,
        [CallerFilePath] string filePath = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        ExceptionDispatchInfo sourceFailure = null;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            sourceFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            logger?.Invoke(task, memberName, filePath, lineNumber);
        }
        catch
        {
            // Diagnostic logging must not replace the source task's outcome.
        }

        sourceFailure?.Throw();
    }

    public static Task Logging(this Task task, Action<Task, string, string, int> logger, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        return task.ContinueWith(delegate (Task x)
        {
            logger(x, memberName, filePath, lineNumber);
        });
    }

    public static Task<T> Logging<T>(this Task<T> task, Action<Task<T>, string, string, int> logger, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        return task.ContinueWith(delegate (Task<T> x)
        {
            logger(x, memberName, filePath, lineNumber);
            return x.Result;
        });
    }

    public static Task<T> Logging<T>(this Task<T> task, Action<Task, string, string, int> logger, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        return task.ContinueWith(delegate (Task<T> x)
        {
            logger(x, memberName, filePath, lineNumber);
            return x.Result;
        });
    }
}
