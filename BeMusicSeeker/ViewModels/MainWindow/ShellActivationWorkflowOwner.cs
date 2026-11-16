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

    private readonly Func<Task<StartupInitializationResult>> initialize;

    private readonly TaskCompletionSource<StartupInitializationResult> startupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int constructedShellActivated;

    private int renderedShellActivated;

    /// <summary>通常構成の更新確認・警告・初期化を一つの実Window起動へ接続します。</summary>
    internal ShellActivationWorkflowOwner(
        StartupUpdateWorkflowOwner startupUpdateWorkflow,
        ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow,
        Func<Task<StartupInitializationResult>> initialize)
    {
        this.startupUpdateWorkflow = startupUpdateWorkflow
            ?? throw new ArgumentNullException(nameof(startupUpdateWorkflow));
        this.elevatedProcessWarningWorkflow = elevatedProcessWarningWorkflow
            ?? throw new ArgumentNullException(nameof(elevatedProcessWarningWorkflow));
        this.initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
    }

    /// <summary>ContentRenderedが実際に開始した初期化の結果・例外・取消だけを転送します。表示呼出しだけでは完了しません。</summary>
    internal Task<StartupInitializationResult> StartupCompletion => startupCompletion.Task;

    /// <summary>
    /// constructorで開始可能な起動処理を一度だけ開始します。
    /// </summary>
    internal bool ActivateConstructedShell()
    {
        if (Interlocked.Exchange(ref constructedShellActivated, 1) != 0)
        {
            return false;
        }

        return startupUpdateWorkflow.Start();
    }

    /// <summary>
    /// ContentRenderedから本番初期化を一度だけ開始し、初期選択・ContextIdle警告の順序を保ちます。
    /// </summary>
    internal Task ActivateRenderedShell(
        Action applyInitialSelection,
        Action<Action> scheduleContextIdle,
        Func<bool> canPresentElevatedProcessWarning)
    {
        if (applyInitialSelection == null)
        {
            throw new ArgumentNullException(nameof(applyInitialSelection));
        }
        if (scheduleContextIdle == null)
        {
            throw new ArgumentNullException(nameof(scheduleContextIdle));
        }
        if (canPresentElevatedProcessWarning == null)
        {
            throw new ArgumentNullException(nameof(canPresentElevatedProcessWarning));
        }
        if (Interlocked.Exchange(ref renderedShellActivated, 1) != 0)
        {
            return Task.CompletedTask;
        }

        Task initializationTask = InitializeAndPublishAsync();
        applyInitialSelection();
        scheduleContextIdle(() => elevatedProcessWarningWorkflow.Start(canPresentElevatedProcessWarning));
        return initializationTask;
    }

    private async Task<bool> InitializeAndPublishAsync()
    {
        try
        {
            StartupInitializationResult result = await initialize();
            startupCompletion.TrySetResult(result);
            return result.Outcome == StartupInitializationOutcome.Succeeded && result.CompletionPublished && result.Failure == null;
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
