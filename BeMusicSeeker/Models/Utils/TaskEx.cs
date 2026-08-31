using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NLog;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using SQLite;

namespace BeMusicSeeker.Models.Utils;

public static class TaskEx
{
    private static readonly Logger loggerLocal = NLogWrapper.FileLogger;

    private static void log2file(Task x, string memberName, string filePath, int lineNumber)
    {
        if (!x.IsFaulted)
        {
            return;
        }
        if (x.Exception != null)
        {
            if (x.Exception.Flatten().InnerExceptions.Count == 1 && x.Exception.Flatten().InnerExceptions.Any(e => e is TaskCanceledException))
            {
                return;
            }
            if (x.Exception.Flatten().InnerExceptions.Any(e => e is PathTooLongException))
            {
                LogTaskFault(x.Exception, "path_too_long", memberName, filePath, lineNumber);
                return;
            }
            if (x.Exception.Flatten().InnerExceptions.Any(e => e is IOException))
            {
                LogTaskFault(x.Exception, "io", memberName, filePath, lineNumber);
                return;
            }
            if (x.Exception.Flatten().InnerExceptions.Any(e => e is OutOfMemoryException))
            {
                LogTaskFault(x.Exception, "out_of_memory", memberName, filePath, lineNumber);
            }
            else if (x.Exception.Flatten().InnerExceptions.Any(e => e is SQLiteException && ((SQLiteException)e).Result == SQLite3.Result.Busy))
            {
                LogTaskFault(x.Exception, "sqlite_busy", memberName, filePath, lineNumber);
            }
            else if (x.Exception.Flatten().InnerExceptions.Any(e => e is SQLiteException && ((SQLiteException)e).Result == SQLite3.Result.Locked))
            {
                LogTaskFault(x.Exception, "sqlite_locked", memberName, filePath, lineNumber);
            }
            else
            {
                LogTaskFault(x.Exception, "unexpected", memberName, filePath, lineNumber);
            }
        }
    }

    private static void LogTaskFault(AggregateException exception, string kind, string memberName, string filePath, int lineNumber)
    {
        try
        {
            string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty;
            string messages = string.Join(" | ", exception.Flatten().InnerExceptions.Select(e => e.Message));
            loggerLocal?.Error(exception, version + " - " + memberName + " (" + filePath + ":" + lineNumber + ") task_fault kind=" + kind + " messages=" + messages + Environment.NewLine + exception);
        }
        catch
        {
        }
    }

    public static Task Logging(this Task task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        return task.Logging(log2file, memberName, filePath, lineNumber);
    }

    /// <summary>
    /// Logs a task fault after the task settles while preserving its success, fault, or cancellation outcome.
    /// </summary>
    /// <param name="task">The task whose outcome remains observable to the caller.</param>
    /// <param name="memberName">The originating member name.</param>
    /// <param name="filePath">The originating source path.</param>
    /// <param name="lineNumber">The originating source line.</param>
    /// <returns>A task that completes with the same outcome as <paramref name="task"/>.</returns>
    public static async Task LoggingAndPropagate(this Task task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }
        await task.LoggingAndPropagate(log2file, memberName, filePath, lineNumber).ConfigureAwait(false);
    }

    public static Task<T> Logging<T>(this Task<T> task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        return task.Logging(log2file, memberName, filePath, lineNumber);
    }
}
