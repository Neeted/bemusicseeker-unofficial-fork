using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 構成済みMainWindowの一回限りの起動と、その実初期化の観測を所有します。
/// </summary>
internal sealed class ShellActivationWorkflowOwner
{
    private readonly StartupUpdateWorkflowOwner startupUpdateWorkflow;

    private readonly ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow;

    private readonly GitHubStarPromptWorkflowOwner gitHubStarPromptWorkflow;

    private readonly Func<Task<StartupInitializationResult>> initialize;

    private readonly TaskCompletionSource<StartupInitializationResult> startupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int constructedShellActivated;

    private int renderedShellActivated;

    /// <summary>通常構成の更新確認・警告・スター依頼・初期化を一つの実Window起動へ接続します。</summary>
    internal ShellActivationWorkflowOwner(
        StartupUpdateWorkflowOwner startupUpdateWorkflow,
        ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow,
        GitHubStarPromptWorkflowOwner gitHubStarPromptWorkflow,
        Func<Task<StartupInitializationResult>> initialize)
    {
        this.startupUpdateWorkflow = startupUpdateWorkflow
            ?? throw new ArgumentNullException(nameof(startupUpdateWorkflow));
        this.elevatedProcessWarningWorkflow = elevatedProcessWarningWorkflow
            ?? throw new ArgumentNullException(nameof(elevatedProcessWarningWorkflow));
        this.gitHubStarPromptWorkflow = gitHubStarPromptWorkflow
            ?? throw new ArgumentNullException(nameof(gitHubStarPromptWorkflow));
        this.initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
    }

    /// <summary>ContentRenderedが実際に開始した初期化の結果・例外・取消だけを転送します。表示呼出しだけでは完了しません。</summary>
    internal Task<StartupInitializationResult> StartupCompletion => startupCompletion.Task;

    /// <summary>
    /// constructorで開始可能な起動処理を一度だけ開始します。更新処理へは、まだ完了していない初期化の観測結果
    /// （<see cref="StartupCompletion"/>）を渡し、更新ZIPの取得を初期化と並行させたまま起動の判断を合流させます。
    /// </summary>
    internal bool ActivateConstructedShell()
    {
        if (Interlocked.Exchange(ref constructedShellActivated, 1) != 0)
        {
            return false;
        }

        return startupUpdateWorkflow.Start(startupCompletion.Task);
    }

    /// <summary>
    /// ContentRenderedから本番初期化を一度だけ開始し、初期選択・ContextIdle警告の順序を保ちます。
    /// スター依頼は初期化の観測結果（<see cref="StartupCompletion"/>）の終端を待って進み、返すTaskはその完了を待ちません。
    /// </summary>
    /// <param name="applyInitialSelection">初期化の開始直後に反映する初期選択。</param>
    /// <param name="scheduleContextIdle">昇格の警告をContextIdleで開始する予約。</param>
    /// <param name="canPresentStartupNotice">起動時の案内（昇格の警告とスター依頼）を表示できる画面の状態かの確認。</param>
    internal Task ActivateRenderedShell(
        Action applyInitialSelection,
        Action<Action> scheduleContextIdle,
        Func<bool> canPresentStartupNotice)
    {
        if (applyInitialSelection == null)
        {
            throw new ArgumentNullException(nameof(applyInitialSelection));
        }
        if (scheduleContextIdle == null)
        {
            throw new ArgumentNullException(nameof(scheduleContextIdle));
        }
        if (canPresentStartupNotice == null)
        {
            throw new ArgumentNullException(nameof(canPresentStartupNotice));
        }
        if (Interlocked.Exchange(ref renderedShellActivated, 1) != 0)
        {
            return Task.CompletedTask;
        }

        Task initializationTask = InitializeAndPublishAsync();
        gitHubStarPromptWorkflow.Start(startupCompletion.Task, canPresentStartupNotice);
        applyInitialSelection();
        scheduleContextIdle(() => elevatedProcessWarningWorkflow.Start(canPresentStartupNotice));
        return initializationTask;
    }

    private async Task<bool> InitializeAndPublishAsync()
    {
        try
        {
            StartupInitializationResult result = await initialize();
            startupCompletion.TrySetResult(result);
            return result.IsCompletedSuccessfully;
        }
        catch (OperationCanceledException failure)
        {
            startupCompletion.TrySetCanceled(failure.CancellationToken);
            throw;
        }
        catch (Exception failure)
        {
            startupCompletion.TrySetException(failure);
            throw;
        }
    }
}
