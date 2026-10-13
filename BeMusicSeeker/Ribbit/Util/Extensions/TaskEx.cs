using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ribbit.Util.Extensions;

public static class TaskEx
{
    /// <summary>
    /// タスクの完了後に診断コールバックを呼び出し、成功、例外、キャンセルを呼出元へ伝播します。診断の失敗は結果を変更しません。
    /// </summary>
    /// <param name="task">完了結果を呼出元へ伝播する対象タスク。</param>
    /// <param name="logger">タスク完了後に呼び出す診断コールバック。</param>
    /// <param name="memberName">呼出元のメンバー名。</param>
    /// <param name="filePath">呼出元のソースパス。</param>
    /// <param name="lineNumber">呼出元のソース行番号。</param>
    /// <returns>対象タスクと同じ成功、例外、またはキャンセルで完了するタスク。</returns>
    public static Task LoggingAndPropagate(
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

        return task.ContinueWith(completedTask =>
        {
            LogCompletedTask(completedTask, logger, memberName, filePath, lineNumber);
            return completedTask;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// タスクの完了後に診断コールバックを呼び出し、成功値、例外、キャンセルを呼出元へ伝播します。診断の失敗は結果を変更しません。
    /// </summary>
    /// <typeparam name="T">タスクが返す値の型。</typeparam>
    /// <param name="task">完了結果を呼出元へ伝播する対象タスク。</param>
    /// <param name="logger">タスク完了後に呼び出す診断コールバック。</param>
    /// <param name="memberName">呼出元のメンバー名。</param>
    /// <param name="filePath">呼出元のソースパス。</param>
    /// <param name="lineNumber">呼出元のソース行番号。</param>
    /// <returns>対象タスクと同じ成功値、例外、またはキャンセルで完了するタスク。</returns>
    public static Task<T> LoggingAndPropagate<T>(
        this Task<T> task,
        Action<Task, string, string, int> logger,
        [CallerMemberName] string memberName = null,
        [CallerFilePath] string filePath = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        return task.ContinueWith(completedTask =>
        {
            LogCompletedTask(completedTask, logger, memberName, filePath, lineNumber);
            return completedTask;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    // 元Taskを再awaitして投げ直すと、FaultedのOCEや複数の失敗を変質させます。
    // 診断後に同じTaskをUnwrapで転送し、取消トークン・元の失敗集合・成功値を保ちます。
    private static void LogCompletedTask(Task task, Action<Task, string, string, int> logger,
        string memberName, string filePath, int lineNumber)
    {
        try { logger?.Invoke(task, memberName, filePath, lineNumber); }
        catch { /* 診断失敗を元Taskの終端へ加えません。 */ }
    }

    /// <summary>
    /// 失敗したタスクを診断し、呼出元へ待機可能な結果を返さずに観測します。診断の失敗は未観測の例外にしません。
    /// </summary>
    /// <param name="task">失敗を観測する対象タスク。Task&lt;T&gt; も指定できます。</param>
    /// <param name="logger">失敗したタスクに対して呼び出す診断コールバック。</param>
    /// <param name="memberName">呼出元のメンバー名。</param>
    /// <param name="filePath">呼出元のソースパス。</param>
    /// <param name="lineNumber">呼出元のソース行番号。</param>
    public static void ObserveFault(
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

        task.ContinueWith(
            completedTask =>
            {
                try
                {
                    if (!completedTask.IsFaulted)
                    {
                        return;
                    }

                    // コールバックが例外を読むとは限らないため、ここで元例外を観測する。
                    _ = completedTask.Exception;
                    logger?.Invoke(completedTask, memberName, filePath, lineNumber);
                }
                catch
                {
                    // 診断コールバックの失敗を未観測の continuation fault にしない。
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

}
