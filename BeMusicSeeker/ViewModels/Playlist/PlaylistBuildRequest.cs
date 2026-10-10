using System;
using System.Threading.Tasks;
namespace BeMusicSeeker.ViewModels;

/// <summary>
/// workerが実buildと表示applyの終端まで所有する、一つのプレイリスト詳細要求です。
/// </summary>
internal sealed class PlaylistBuildRequest
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>この受理済み要求の実build、表示applyとcleanupの終端です。日常coalescingでも同じ本体を追跡します。</summary>
    internal Task Completion { get; private set; }

    /// <summary>日常要求と必須UI要求で共通の実終端Taskを作ります。非待機の日常呼出しの失敗も観測します。</summary>
    internal PlaylistBuildRequest()
    {
        Completion = completion.Task;
        _ = Completion.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>同じ入力への既存要求だけに合流します。新しい別選択の全buildは待ちません。</summary>
    internal void JoinCompletion(PlaylistBuildRequest existing) => Completion = existing.Completion;

    /// <summary>未開始の置換と、実body/cleanupの終了を終端します。実失敗は後発のobsolete/終了で隠しません。</summary>
    internal void Complete(Exception failure = null)
    {
        if (failure == null) { completion.TrySetResult(); }
        else { completion.TrySetException(failure); }
    }

    internal int RequestVersion;

    internal long MainViewBuildRequestId;

    internal MainViewUpdateMode Mode;

    internal MainViewUpdateMode RequestedMode;

    internal ChartListFilterSnapshot Filters;

    internal ChartListSortParameters SortParameters;

    internal MainViewUpdateMode CurrentTreeMode;

    internal PlaylistOpenReadinessSnapshot OpenReadiness;

    internal PlaylistRequestIdentity Identity;

    internal bool UseCoalescingWindow;

    internal int LastBuiltScoreSnapshotVersion;

    internal string SourceInvalidationReason;
}
