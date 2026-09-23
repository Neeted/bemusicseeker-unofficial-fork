using System;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class TestStartupProgressOwnerFactory
{
    /// <summary>
    /// 同期実行を既定とし、必要に応じて表示反映の完了を観測できる起動進捗の管理主体を作ります。
    /// </summary>
    /// <param name="backgroundTasksIdle">必須処理が残っていないことを返します。</param>
    /// <param name="requiredInitializationSchedulingComplete">必須処理の登録が閉じたことを返します。</param>
    /// <param name="completionHideDelay">完了した操作の表示を消す時点を制御します。</param>
    /// <param name="dispatch">表示反映の実行境界。省略時はその場で同期実行します。</param>
    /// <returns>他のテストから独立した起動進捗の管理主体。</returns>
    internal static StartupProgressWorkflowOwner Create(
        Func<Task>? completionHideDelay = null,
        Func<bool>? backgroundTasksIdle = null,
        Func<bool>? requiredInitializationSchedulingComplete = null,
        Action<Action>? dispatch = null)
    {
        backgroundTasksIdle ??= () => false;
        requiredInitializationSchedulingComplete ??= () => true;
        return new StartupProgressWorkflowOwner(
            () => new StartupProgressVersionSnapshot(),
            (_, _) => { },
            dispatch ?? (action => action()),
            _ => { },
            _ => { },
            backgroundTasksIdle,
            (generation, revision) => backgroundTasksIdle(),
            requiredInitializationSchedulingComplete,
            new object(),
            completionHideDelay);
    }
}
