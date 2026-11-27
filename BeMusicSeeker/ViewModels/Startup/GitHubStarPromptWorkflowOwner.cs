#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>GitHubのスター依頼の一回の起動での終端です。診断とテストの観測に使い、利用者へは通知しません。</summary>
internal enum GitHubStarPromptWorkflowOutcome
{
    /// <summary>構成で無効です。保存値に触れていません。</summary>
    Disabled,

    /// <summary>数える起動に当たりませんでした。</summary>
    NotCounted,

    /// <summary>依頼の終了後か表示の上限に達しています。</summary>
    Finished,

    /// <summary>保存値の読取りか、起動の回数・予定の保存に失敗しました。</summary>
    CountSaveFailed,

    /// <summary>LR2の失敗、LR2ルート未設定の警告、更新なし以外の更新確認の終端のいずれかで表示を見送りました。</summary>
    SessionConditionUnmet,

    /// <summary>保存値から見て、まだ表示できません。</summary>
    NotDue,

    /// <summary>画面の状態により表示しませんでした。</summary>
    NotPresented,

    /// <summary>表示を依頼できなかったか、表示が失敗しました。</summary>
    PresentationFailed,

    /// <summary>表示が成立し、選択を保存しました。</summary>
    Answered,

    /// <summary>表示が成立しましたが、選択の保存に失敗しました。</summary>
    ChoiceSaveFailed
}

/// <summary>
/// GitHubのスター依頼の表示要求です。画面側は <see cref="Complete"/>、<see cref="CompleteNotPresented"/>、
/// <see cref="Fail"/> のいずれかで一度だけ結果を返します。
/// </summary>
internal sealed class GitHubStarPromptPresentationRequest
{
    private readonly TaskCompletionSource<GitHubStarPromptChoice?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>表示が成立した場合は選択、成立しなかった場合はnullを返すTaskです。</summary>
    internal Task<GitHubStarPromptChoice?> Completion => completion.Task;

    /// <summary>表示が成立し、利用者が選んだ操作を返します。</summary>
    internal void Complete(GitHubStarPromptChoice choice)
    {
        completion.TrySetResult(choice);
    }

    /// <summary>終了中などで表示しなかったことを返します。保存値は変えません。</summary>
    internal void CompleteNotPresented()
    {
        completion.TrySetResult(null);
    }

    /// <summary>表示の失敗を返します。保存値は変えません。</summary>
    internal void Fail(Exception exception)
    {
        completion.TrySetException(exception ?? new InvalidOperationException("GitHub star prompt presentation failed."));
    }
}

/// <summary>
/// 起動の完了後に、GitHubのスター依頼の計数・表示可否・表示依頼・選択の保存を一度だけ行います。
/// 画面は表示要求のイベントを受ける表示の窓口として扱います。
/// </summary>
/// <remarks>
/// 任意の案内なので、起動の結果・解禁・追加処理を待たせず、失敗は局所の診断に留めます。
/// 計数の保存は更新確認の終端を待つ前に行い、更新を案内した起動でも起動の回数を失わないようにします。
/// 表示できるかの確認とイベントの発行は、WPFの状態を読むため <c>tryDispatchToUi</c> が渡すUIスレッドで行います。
/// 表示中のモーダル画面がメイン画面の閉じる操作を止めるため、終了処理の受付は持ちません。
/// </remarks>
internal sealed class GitHubStarPromptWorkflowOwner
{
    private readonly bool enabled;

    private readonly IGitHubStarPromptStore store;

    private readonly Func<DateTimeOffset> utcNow;

    private readonly Func<Task<StartupUpdateWorkflowOutcome?>> waitForUpdateTerminal;

    private readonly Func<Action, bool> tryDispatchToUi;

    private readonly Action<Exception, string>? warningLog;

    private readonly TaskCompletionSource<GitHubStarPromptWorkflowOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int started;

    /// <summary>保存値・時刻・更新確認・UIへの受け渡しの境界を接続します。</summary>
    /// <param name="enabled">偽なら保存値に触れず何もしません。検証起動では偽です。</param>
    /// <param name="store">保存値の窓口。</param>
    /// <param name="utcNow">現在のUTC時刻。</param>
    /// <param name="waitForUpdateTerminal">起動時の更新確認の終端を待ち、その種類を返します。終端の記録がなければnullです。</param>
    /// <param name="tryDispatchToUi">UIスレッドへ処理を渡します。受け付けなかった場合は偽を返します。</param>
    /// <param name="warningLog">局所の診断の出力先。</param>
    internal GitHubStarPromptWorkflowOwner(
        bool enabled,
        IGitHubStarPromptStore store,
        Func<DateTimeOffset> utcNow,
        Func<Task<StartupUpdateWorkflowOutcome?>> waitForUpdateTerminal,
        Func<Action, bool> tryDispatchToUi,
        Action<Exception, string>? warningLog = null)
    {
        this.enabled = enabled;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.waitForUpdateTerminal = waitForUpdateTerminal ?? throw new ArgumentNullException(nameof(waitForUpdateTerminal));
        this.tryDispatchToUi = tryDispatchToUi ?? throw new ArgumentNullException(nameof(tryDispatchToUi));
        this.warningLog = warningLog;
    }

    /// <summary>表示を画面へ依頼します。購読者は要求へ一度だけ結果を返します。</summary>
    internal event Action<GitHubStarPromptPresentationRequest>? PresentationRequested;

    /// <summary>この起動での終端です。開始しなければ完了しません。</summary>
    internal Task<GitHubStarPromptWorkflowOutcome> Completion => completion.Task;

    /// <summary>
    /// 起動の初期化の実Taskの終端を待ってから進むスター依頼の処理を、一度だけ開始します。呼出元は待たされません。
    /// </summary>
    /// <param name="initialization">通常起動の初期化の観測結果。スター依頼の処理がこの終端を待ちます。初期化そのものは止めません。</param>
    /// <param name="canPresent">表示の直前にUIスレッドで呼ぶ、画面が表示できる状態かの確認。</param>
    /// <returns>開始した場合は真。開始済みなら偽です。</returns>
    internal bool Start(Task<StartupInitializationResult> initialization, Func<bool> canPresent)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        ArgumentNullException.ThrowIfNull(canPresent);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return false;
        }

        _ = RunAsync(initialization, canPresent);
        return true;
    }

    private async Task RunAsync(Task<StartupInitializationResult> initialization, Func<bool> canPresent)
    {
        GitHubStarPromptWorkflowOutcome outcome;
        try
        {
            outcome = await ExecuteAsync(initialization, canPresent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "github_star_prompt workflow failed");
            outcome = GitHubStarPromptWorkflowOutcome.PresentationFailed;
        }
        completion.TrySetResult(outcome);
    }

    private async Task<GitHubStarPromptWorkflowOutcome> ExecuteAsync(Task<StartupInitializationResult> initialization, Func<bool> canPresent)
    {
        if (!enabled)
        {
            return GitHubStarPromptWorkflowOutcome.Disabled;
        }

        StartupInitializationResult result;
        try
        {
            result = await initialization.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 初期化の失敗・取消の通知は起動の所有者が行います。
            return GitHubStarPromptWorkflowOutcome.NotCounted;
        }
        if (result == null || !result.IsCompletedSuccessfully || result.OperationKind != StartupProgressOperationKind.Startup)
        {
            return GitHubStarPromptWorkflowOutcome.NotCounted;
        }

        GitHubStarPromptRecord counted;
        try
        {
            GitHubStarPromptRecord saved = store.Read();
            if (!GitHubStarPromptPolicy.IsActive(saved))
            {
                return GitHubStarPromptWorkflowOutcome.Finished;
            }
            counted = GitHubStarPromptPolicy.RecordCountedStartup(saved, utcNow());
            if (counted != saved)
            {
                store.Save(counted);
            }
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "github_star_prompt startup count save failed");
            return GitHubStarPromptWorkflowOutcome.CountSaveFailed;
        }

        StartupUpdateWorkflowOutcome? updateOutcome;
        try
        {
            updateOutcome = await waitForUpdateTerminal().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "github_star_prompt update terminal wait failed");
            return GitHubStarPromptWorkflowOutcome.SessionConditionUnmet;
        }
        if (updateOutcome != StartupUpdateWorkflowOutcome.NoUpdate
            || result.RequiredResult?.Lr2Failure != null
            || result.ShowRootWarning)
        {
            return GitHubStarPromptWorkflowOutcome.SessionConditionUnmet;
        }
        if (!GitHubStarPromptPolicy.CanShow(counted, utcNow()))
        {
            return GitHubStarPromptWorkflowOutcome.NotDue;
        }

        GitHubStarPromptChoice? choice;
        try
        {
            choice = await PresentAsync(canPresent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "github_star_prompt presentation failed");
            return GitHubStarPromptWorkflowOutcome.PresentationFailed;
        }
        if (choice is not { } selected)
        {
            return GitHubStarPromptWorkflowOutcome.NotPresented;
        }

        try
        {
            store.Save(GitHubStarPromptPolicy.ApplyChoice(counted, selected, utcNow()));
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "github_star_prompt choice save failed");
            return GitHubStarPromptWorkflowOutcome.ChoiceSaveFailed;
        }
        return GitHubStarPromptWorkflowOutcome.Answered;
    }

    private Task<GitHubStarPromptChoice?> PresentAsync(Func<bool> canPresent)
    {
        var request = new GitHubStarPromptPresentationRequest();
        bool dispatched = tryDispatchToUi(() =>
        {
            try
            {
                if (!canPresent())
                {
                    request.CompleteNotPresented();
                    return;
                }
                Action<GitHubStarPromptPresentationRequest>? handler = PresentationRequested;
                if (handler == null)
                {
                    request.Fail(new InvalidOperationException("GitHub star prompt presentation is not connected."));
                    return;
                }
                handler(request);
            }
            catch (Exception exception)
            {
                request.Fail(exception);
            }
        });
        if (!dispatched)
        {
            request.CompleteNotPresented();
        }
        return request.Completion;
    }

    private void LogWarningSafely(Exception exception, string context)
    {
        try
        {
            warningLog?.Invoke(exception, context);
        }
        catch
        {
            // 任意の案内の診断の失敗を、起動や別の通知へ伝えません。
        }
    }
}
