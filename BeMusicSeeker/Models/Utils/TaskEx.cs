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

    /// <summary>
    /// タスクの完了後にアプリケーションログへ診断情報を出力し、成功、例外、キャンセルを呼出元へ伝播します。ログの失敗は結果を変更しません。
    /// </summary>
    /// <param name="task">完了結果を呼出元へ伝播する対象タスク。</param>
    /// <param name="memberName">呼出元のメンバー名。</param>
    /// <param name="filePath">呼出元のソースパス。</param>
    /// <param name="lineNumber">呼出元のソース行番号。</param>
    /// <returns>対象タスクと同じ成功、例外、またはキャンセルで完了するタスク。</returns>
    public static async Task LoggingAndPropagate(this Task task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }
        await task.LoggingAndPropagate(log2file, memberName, filePath, lineNumber).ConfigureAwait(false);
    }

    /// <summary>
    /// タスクの完了後にアプリケーションログへ診断情報を出力し、成功値、例外、キャンセルを呼出元へ伝播します。ログの失敗は結果を変更しません。
    /// </summary>
    /// <typeparam name="T">タスクが返す値の型。</typeparam>
    /// <param name="task">完了結果を呼出元へ伝播する対象タスク。</param>
    /// <param name="memberName">呼出元のメンバー名。</param>
    /// <param name="filePath">呼出元のソースパス。</param>
    /// <param name="lineNumber">呼出元のソース行番号。</param>
    /// <returns>対象タスクと同じ成功値、例外、またはキャンセルで完了するタスク。</returns>
    public static async Task<T> LoggingAndPropagate<T>(this Task<T> task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }
        return await Ribbit.Util.Extensions.TaskEx.LoggingAndPropagate(task, log2file, memberName, filePath, lineNumber).ConfigureAwait(false);
    }

    /// <summary>
    /// タスクの失敗だけをアプリケーションログへ記録し、待機可能な結果を返さずに観測します。ログの失敗は未観測の例外にしません。
    /// </summary>
    /// <param name="task">失敗を観測する対象タスク。</param>
    /// <param name="memberName">呼出元のメンバー名。</param>
    /// <param name="filePath">呼出元のソースパス。</param>
    /// <param name="lineNumber">呼出元のソース行番号。</param>
    public static void ObserveFault(this Task task, [CallerMemberName] string memberName = null, [CallerFilePath] string filePath = null, [CallerLineNumber] int lineNumber = 0)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }
        Ribbit.Util.Extensions.TaskEx.ObserveFault(task, log2file, memberName, filePath, lineNumber);
    }

}
