using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>整理候補一覧からの整理の結末です。</summary>
internal enum AudioDuplicateOrganizeRunStatus
{
    /// <summary>対象のフォルダが0件で、受付・確認・案内・進捗なしに終えました。</summary>
    NoTargets,

    /// <summary>対象に未確認のフォルダがあったため、先に確認を行う案内を一件だけ出して終えました。受付の取得・確認・再生停止・モデルの呼出しを行わず、状態を変えていません。</summary>
    UnverifiedTargets,

    /// <summary>共通のライブラリ変更受付が使用中で取得できず、Busyを案内して状態を変えずに終えました。</summary>
    Busy,

    /// <summary>共通のライブラリ変更受付が閉じている（終了の要求中など）ため、案内なしに状態を変えずに終えました。</summary>
    NotStarted,

    /// <summary>確認でキャンセルまたは閉じられ、状態を変えずに終えました。</summary>
    Declined,

    /// <summary>確認の表示失敗、再生停止の失敗、モデルの例外など、モデルの結果を得られずに終えました。失敗は一回だけ報告済みで、結果は案内しません。</summary>
    Failed,

    /// <summary>モデルが操作全体を受理しませんでした（カタログ未収束・管理出力の保護）。モデルが積んだ案内だけを表示済みで、結果は案内しません。</summary>
    NotAdmitted,

    /// <summary>モデルの結果を得て、結果を一回案内しました。取消・一部の失敗を含みます。</summary>
    Completed
}

/// <summary>整理候補一覧からの整理の結果です。</summary>
/// <param name="Status">結末。</param>
/// <param name="Result">モデルの結果。得られなかった結末ではnullです。</param>
/// <param name="Failure">報告した失敗。ないときはnullです。</param>
internal sealed record AudioDuplicateOrganizeRunResult(
    AudioDuplicateOrganizeRunStatus Status, AudioDuplicateOrganizeResult Result = null, Exception Failure = null)
{
    internal static AudioDuplicateOrganizeRunResult NoTargets { get; } = new(AudioDuplicateOrganizeRunStatus.NoTargets);

    internal static AudioDuplicateOrganizeRunResult UnverifiedTargets { get; } = new(AudioDuplicateOrganizeRunStatus.UnverifiedTargets);

    internal static AudioDuplicateOrganizeRunResult Busy { get; } = new(AudioDuplicateOrganizeRunStatus.Busy);

    internal static AudioDuplicateOrganizeRunResult NotStarted { get; } = new(AudioDuplicateOrganizeRunStatus.NotStarted);

    internal static AudioDuplicateOrganizeRunResult Declined { get; } = new(AudioDuplicateOrganizeRunStatus.Declined);
}

/// <summary>重複音源の整理を、受け付けた共通受付の下で実行する窓口です。テストで結果と失敗を注入できるよう、操作の窓口として分けています。</summary>
internal interface IAudioDuplicateOrganizeStore
{
    /// <summary>
    /// 整理を実行します。呼出し側が受理済みの共通受付を保持している間に、UIの文脈の外で呼びます。
    /// 受付は <paramref name="capability"/> で渡し、モデルは解放しません。
    /// </summary>
    AudioDuplicateOrganizeResult Organize(
        BMSLibrary library,
        AudioDuplicateOrganizeRequest request,
        long operationId,
        LibraryOperationNotices notices,
        Action<AudioDuplicateOrganizeProgress> progress,
        CancellationToken cancellationToken,
        LibraryFileMutationCapability capability);
}

/// <summary>実ライブラリへ整理を委譲する窓口です。</summary>
internal sealed class BmsLibraryAudioDuplicateOrganizeStore : IAudioDuplicateOrganizeStore
{
    /// <inheritdoc />
    public AudioDuplicateOrganizeResult Organize(
        BMSLibrary library,
        AudioDuplicateOrganizeRequest request,
        long operationId,
        LibraryOperationNotices notices,
        Action<AudioDuplicateOrganizeProgress> progress,
        CancellationToken cancellationToken,
        LibraryFileMutationCapability capability)
        => library.OrganizeAudioDuplicateFolders(request, operationId, notices, progress, cancellationToken, capability);
}

/// <summary>
/// 整理候補一覧の「選択フォルダを整理」の手順を管理します。表示中でSELECTがオンのフォルダを一つの要求にまとめ、対象に未確認があれば案内だけで終え、
/// そうでなければ共通のライブラリ変更受付を非待機で取得して、常に一度だけ確認し、承認後は取消できる進捗画面の内側で、再生の停止とモデルの整理（UIの文脈の外）を行います。
/// </summary>
/// <remarks>
/// 受付は、モデルの終端まで保持し、結果の案内より前に解放します。受付を保持したまま、UIの完了を同期的に待ちません。
/// 取消は、フォルダの開始前にだけ効き、処理中のフォルダを終えてから止まります（進捗画面は取消の扱いを操作へ任せます）。
/// モデルが操作を受理して結果を返したら、対象のうち「未処理」以外のフォルダのSELECTを、整理を始めた一覧でオフにします。
/// 確認を再実行せず、確認結果を書き換えません。整理後の一覧の作り直しは、モデルの結果の変更通知を受けた画面が行います。
/// </remarks>
internal sealed class AudioDuplicateOrganizeWorkflowOwner
{
    /// <summary>進捗画面の取消の要求を、モデルの取消の合図へ伝える間隔です。</summary>
    private static readonly TimeSpan CancellationPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly AudioDuplicateCandidateListOwner list;
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer operationAdmission;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IChartMutationPlaybackPort playback;
    private readonly IUiDialogService dialogs;
    private readonly IAudioDuplicateOrganizeStore store;

    /// <param name="list">整理候補一覧の管理主体。対象の捕捉とSELECTの消費に使います。</param>
    /// <param name="libraryProvider">現在のライブラリを返します。</param>
    /// <param name="operationAdmission">共通のライブラリ変更受付。</param>
    /// <param name="chartMutationActivity">譜面変更の活動の窓口。</param>
    /// <param name="playback">再生の停止の窓口。</param>
    /// <param name="dialogs">確認・進捗・案内の窓口。</param>
    /// <param name="store">整理の窓口。省略時は実ライブラリへ委譲します。</param>
    internal AudioDuplicateOrganizeWorkflowOwner(
        AudioDuplicateCandidateListOwner list,
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer operationAdmission,
        ChartMutationActivityOwner chartMutationActivity,
        IChartMutationPlaybackPort playback,
        IUiDialogService dialogs,
        IAudioDuplicateOrganizeStore store = null)
    {
        this.list = list ?? throw new ArgumentNullException(nameof(list));
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.operationAdmission = operationAdmission ?? throw new ArgumentNullException(nameof(operationAdmission));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.store = store ?? new BmsLibraryAudioDuplicateOrganizeStore();
    }

    /// <summary>整理の実行状態です。進捗画面の別スレッドと呼出し側が共有するため、読み書きは明示的に行います。</summary>
    private sealed class Run(AudioDuplicateOrganizeRequest request, IReadOnlyList<AudioDuplicateCandidateTarget> targets, BMSLibrary library)
    {
        internal AudioDuplicateOrganizeRequest Request { get; } = request;
        internal IReadOnlyList<AudioDuplicateCandidateTarget> Targets { get; } = targets;
        internal BMSLibrary Library { get; } = library;
        internal LibraryOperationNotices Notices { get; } = new();
        internal Task Execution;
        internal AudioDuplicateOrganizeResult ModelResult;
        internal AudioDuplicateOrganizeProgress? Latest;
        internal (int Percent, string Message)? Posted;
    }

    /// <summary>ツールバーの「選択フォルダを整理」です。対象が0件なら、受付・確認・案内・再生停止・進捗・モデルの呼出しのどれも行いません。</summary>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal async Task<AudioDuplicateOrganizeRunResult> OrganizeSelectedAsync(Window owner = null)
    {
        IReadOnlyList<AudioDuplicateCandidateTarget> targets = list.CaptureSelectedFolders();
        if (targets.Count == 0) { return AudioDuplicateOrganizeRunResult.NoTargets; }
        if (targets.Any(target => target.IsUnverified))
        {
            // 状態を変えず、受付も取らずに案内だけで終える。
            try
            {
                UiDialogResult notice = await dialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(
                    Resources.Audio_duplicate_organize_unverified_notice, Resources.Warning));
                UiDialogRoute.ThrowIfNotShown(notice, "Audio duplicate organize unverified notification");
                return AudioDuplicateOrganizeRunResult.UnverifiedTargets;
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(exception);
                return new AudioDuplicateOrganizeRunResult(AudioDuplicateOrganizeRunStatus.Failed, Failure: exception);
            }
        }
        if (!operationAdmission.TryEnter(out IDisposable lease))
        {
            // 受付の閉鎖（終了の要求中など）による不成立は案内しない。他の受付を使う手順と同じ扱いにする。
            if (operationAdmission.IsAdmissionClosed) { return AudioDuplicateOrganizeRunResult.NotStarted; }
            try
            {
                UiDialogResult busy = await dialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(
                    Resources.Warn_LibraryOperationBusy, Resources.Warning));
                UiDialogRoute.ThrowIfNotShown(busy, "Audio duplicate organize Busy notification");
                return AudioDuplicateOrganizeRunResult.Busy;
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(exception);
                return new AudioDuplicateOrganizeRunResult(AudioDuplicateOrganizeRunStatus.Failed, Failure: exception);
            }
        }

        Run run = null;
        Exception failure = null;
        AudioDuplicateOrganizeRunResult early = null;
        try
        {
            BMSLibrary library = libraryProvider();
            if (library == null) { throw new InvalidOperationException("Audio duplicate organize library is not available."); }
            run = new Run(AudioDuplicateOrganizePresentation.CreateRequest(targets), targets, library);
            bool? accepted = await ConfirmAsync(targets, e => failure = e);
            if (accepted == null) { early = new AudioDuplicateOrganizeRunResult(AudioDuplicateOrganizeRunStatus.Failed, Failure: failure); }
            else if (accepted == false) { early = AudioDuplicateOrganizeRunResult.Declined; }
            else { failure = await RunAcceptedAsync(run, lease, owner); }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            // 実行を開始した場合も、モデルの終端（実行のTaskの完了）の後にここで解放し、結果の案内の前に受付を返す。
            lease.Dispose();
        }

        if (early != null)
        {
            if (early.Failure != null) { await ReportFailureAsync(early.Failure); }
            return early;
        }
        return await CompleteAsync(run, failure);
    }

    /// <summary>
    /// 整理の確認を表示します。確認を表示しない設定（<c>ShowDuplicateFileCheckConfirmMsg</c>）でも、常に一度表示します。
    /// OKとキャンセルがあり、既定はキャンセルです。要注意のフォルダを含むときだけ、続行のチェックを入れるまでOKを無効にします。警告アイコンと警告用の本文は、
    /// 要注意のフォルダを含むとき、または削除方法が完全に削除のとき（先頭に元に戻せない旨の行）に表示します。
    /// </summary>
    /// <returns>承認は true、キャンセル・閉じるは false、表示の失敗は null（失敗は <paramref name="failed"/> へ渡します）。</returns>
    private async Task<bool?> ConfirmAsync(IReadOnlyList<AudioDuplicateCandidateTarget> targets, Action<Exception> failed)
    {
        try
        {
            string message = AudioDuplicateOrganizePresentation.CreateConfirmationMessage(targets, out bool hasWarning, out string warningMessage);
            // 完全に削除は元に戻せないため、他の警告がなくてもアイコンを警告にします。続行のチェックは従来の条件（要注意）のままです。
            bool permanentDelete = AudioDuplicateOrganizePresentation.GetDeleteMethod(targets) == DuplicateOrganizeDeleteMethod.DeletePermanently;
            UiDialogResult result = await dialogs.ConfirmAsync(
                new UiConfirmationRequest(
                    message,
                    Resources.Confirm,
                    MessageBoxButton.OKCancel,
                    hasWarning || permanentDelete ? MessageBoxImage.Warning : MessageBoxImage.Question,
                    MessageBoxResult.Cancel,
                    warningMessageBoxText: warningMessage)
                {
                    // 利用者の辞書が空でも、要注意のフォルダの整理に必要な確認を省略しません。
                    AcknowledgmentText = hasWarning ? Resources.Audio_duplicate_organize_acknowledgment ?? string.Empty : null
                });
            if (result == null)
            {
                failed(new InvalidOperationException("Audio duplicate organize confirmation returned no result."));
                return null;
            }
            switch (result.Status)
            {
                case UiDialogStatus.Accepted:
                    return true;
                case UiDialogStatus.Rejected:
                case UiDialogStatus.CancelledByUser:
                    return false;
                case UiDialogStatus.ClosedByUser:
                    return result.IsPositive;
                default:
                    failed(new InvalidOperationException(
                        "Audio duplicate organize confirmation could not be displayed (" + result.Status + ").", result.Exception));
                    return null;
            }
        }
        catch (Exception exception)
        {
            failed(exception);
            return null;
        }
    }

    /// <summary>
    /// 進捗画面を開き、その操作の内側で整理を実行し、実行の終端まで待ちます。画面の経路が失敗した場合の例外を返します（成功と取消はnullです）。
    /// 実行の失敗（再生停止の失敗・モデルの例外）も返します。
    /// </summary>
    private async Task<Exception> RunAcceptedAsync(Run run, IDisposable lease, Window owner)
    {
        Exception failure = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            UiProgressResult progressResult = await dialogs.RunWithProgressAsync(
                new UiProgressRequest(
                    Resources.Audio_duplicate_organize_title,
                    Resources.Audio_duplicate_organize_progress_label,
                    new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false)
                    {
                        // 取消の要求中でも操作の例外と結果を失わないよう、取消の扱いを操作へ任せます。
                        OperationOwnsCancellation = true
                    },
                    owner),
                context => RunOperationAsync(run, context, lease, cancellation));
            if (progressResult == null)
            {
                failure = new InvalidOperationException("Audio duplicate organize progress route returned no result.");
            }
            else if (progressResult.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser))
            {
                failure = new InvalidOperationException(
                    "Audio duplicate organize progress route failed: " + progressResult.Status, progressResult.Error);
            }
            else if (run.Execution == null)
            {
                failure = new InvalidOperationException("Audio duplicate organize progress route did not run the operation.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        // 実行の終端まで待つ。進捗画面が先に閉じても、受付は呼出し側がモデルの終端の後に解放する。
        if (run.Execution != null)
        {
            if (failure != null) { cancellation.Cancel(); }
            try { await run.Execution; }
            catch (Exception exception) { failure ??= exception; }
        }
        return failure;
    }

    /// <summary>
    /// 進捗画面の操作です。UIの文脈の外で、再生の停止、モデルの整理の順に実行します。取消ボタンの押下は、モデルの取消の合図へ伝えます。
    /// 操作の開始前と再生の停止の直後（モデルの呼出しの直前）には必ず確認し、再生の停止の前後に要求された取消は、取消済みの合図でモデルを呼びます。
    /// モデルの実行中は一定間隔で確認します。実行の終端まで画面を閉じません。
    /// </summary>
    private async Task RunOperationAsync(Run run, UiProgressContext context, IDisposable lease, CancellationTokenSource cancellation)
    {
        if (context.CancellationRequested) { cancellation.Cancel(); }
        CancellationToken token = cancellation.Token;
        run.Execution = Task.Run(async () =>
        {
            IDisposable activity = null;
            try
            {
                activity = chartMutationActivity.Enter();
                // 再生の停止の失敗では、モデルを呼ばない。
                await playback.StopPlaybackForMutationAsync().ConfigureAwait(false);
                // 再生の停止の間に押された取消を、モデルを呼ぶ直前に合図へ反映する。画面側の再確認（一定間隔）を待つと、
                // その間にモデルが最初のフォルダの処理を始め得る。取消の要求は進捗画面の状態を読むだけで、UIスレッドを待たない。
                if (context.CancellationRequested) { cancellation.Cancel(); }
                using LibraryFileMutationCapability capability = operationAdmission.CreateMutationCapability(lease);
                run.ModelResult = store.Organize(
                    run.Library,
                    run.Request,
                    Stopwatch.GetTimestamp(),
                    run.Notices,
                    progress => ReportProgress(run, progress),
                    token,
                    capability);
            }
            finally
            {
                activity?.Dispose();
            }
        });
        while (!run.Execution.IsCompleted)
        {
            if (context.CancellationRequested) { cancellation.Cancel(); }
            PostLatestProgress(run, context);
            await Task.WhenAny(run.Execution, Task.Delay(CancellationPollInterval)).ConfigureAwait(false);
        }
        PostLatestProgress(run, context);
        // 結果と例外は、呼出し側が実行のTaskから読みます。画面の操作としては、終端まで待つだけです。
        try { await run.Execution.ConfigureAwait(false); }
        catch { /* 呼出し側が同じTaskから失敗を読みます。 */ }
    }

    /// <summary>モデルの進捗を受け取ります。待たずに戻り、最新の値だけを保持して、画面への投稿は <see cref="PostLatestProgress"/> が間引きます。</summary>
    private static void ReportProgress(Run run, AudioDuplicateOrganizeProgress progress)
    {
        lock (run)
        {
            if (run.Latest is { } latest && progress.Number < latest.Number) { return; }
            run.Latest = progress;
        }
    }

    /// <summary>保持している最新の値を、前回投稿した値と異なる場合だけ画面へ投稿します。投稿は呼出し側の一つの流れだけが行います。</summary>
    private static void PostLatestProgress(Run run, UiProgressContext context)
    {
        AudioDuplicateOrganizeProgress latest;
        lock (run)
        {
            if (run.Latest is not { } value) { return; }
            latest = value;
        }
        int percent = 100 * Math.Max(latest.Number - 1, 0) / Math.Max(latest.Total, 1);
        string message = AudioDuplicateOrganizePresentation.CreateProgressMessage(latest);
        if (run.Posted == (percent, message)) { return; }
        run.Posted = (percent, message);
        context.Report(percent, message);
    }

    /// <summary>
    /// 実行の終端の後の報告です。モデルの結果がない経路（再生停止の失敗・モデルの例外・進捗の経路の失敗）は報告を一回だけ出して結果を案内せず、
    /// モデルが受理しなかった場合はモデルが積んだ案内だけを出し、受理した結果は最後に一度だけ案内します。受付は呼出し側が解放済みです。
    /// </summary>
    private async Task<AudioDuplicateOrganizeRunResult> CompleteAsync(Run run, Exception failure)
    {
        AudioDuplicateOrganizeResult model = run?.ModelResult;
        // 案内を持つ操作は、受付の解放後に依頼する。表示の完了は待たず、失敗は結果を変えない。
        if (run != null)
        {
            BMSLibrary library = run.Library;
            await FileDbMutationReport.BeginRequestOperationNotices(
                dialogs, run.Notices, () => library?.IsShutdownRequested == true, "audioDuplicateOrganize").ConfigureAwait(true);
        }
        if (model == null || failure != null)
        {
            if (failure != null) { await ReportFailureAsync(failure); }
            if (model == null) { return new AudioDuplicateOrganizeRunResult(AudioDuplicateOrganizeRunStatus.Failed, null, failure); }
        }
        if (!model.Admitted)
        {
            return new AudioDuplicateOrganizeRunResult(AudioDuplicateOrganizeRunStatus.NotAdmitted, model, failure);
        }
        ConsumeSelectOfProcessedFolders(run, model);
        await ShowResultAsync(model, run.Request.DeleteMethod);
        return new AudioDuplicateOrganizeRunResult(AudioDuplicateOrganizeRunStatus.Completed, model, failure);
    }

    /// <summary>
    /// モデルが操作を受理して結果を返したとき、対象のフォルダのうち「未処理」以外のSELECTを、整理を始めた一覧でオフにします。UIスレッドで呼びます。
    /// 取消や未適用で着手しなかったフォルダは変えず、確認結果も書き換えません。一覧の状態表にないフォルダの書込みは捨てます。
    /// </summary>
    private void ConsumeSelectOfProcessedFolders(Run run, AudioDuplicateOrganizeResult model)
    {
        int count = Math.Min(run.Targets.Count, model.Folders.Count);
        var processed = new List<DuplicateGroup>(count);
        for (int index = 0; index < count; index++)
        {
            if (model.Folders[index].Status != AudioDuplicateOrganizeStatus.Unprocessed) { processed.Add(run.Targets[index].Group); }
        }
        list.ClearSelectOfGroups(processed);
        // 整理で解消したフォルダの行と状態は、画面の表示中かによらずここで取り除く（ノードが消えて画面が移った後も、行を残さない）。
        if (run.Library.AudioDuplicateFolders is { } remaining) { list.RemoveUnresolvedFolders(remaining.Select(folder => folder.Group)); }
    }

    /// <summary>結果を一度だけ案内します。表示の失敗は診断へ残すだけで、整理の結果を変えません。</summary>
    private async Task ShowResultAsync(AudioDuplicateOrganizeResult model, DuplicateOrganizeDeleteMethod deleteMethod)
    {
        try
        {
            string message = AudioDuplicateOrganizePresentation.CreateResultMessage(model, deleteMethod, out bool allCompleted);
            UiDialogResult result = await dialogs.ShowMessageAsync(UiMessageRequest.CreateInformation(
                message, Resources.Audio_duplicate_organize_title, completedSuccessfully: allCompleted));
            UiDialogRoute.ThrowIfNotShown(result, "Audio duplicate organize result notification");
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }

    /// <summary>確認以外で終わった失敗を、報告で一度だけ表示します。表示の失敗は診断へ残すだけです。</summary>
    private Task ReportFailureAsync(Exception failure)
        => FileDbMutationReport.ShowAsync(
            dialogs, Resources.Audio_duplicate_organize_title, LibraryMutationSessionReceipt.Empty, failure, mergeOperation: false);
}

/// <summary>整理候補一覧からの整理の要求・確認文・進捗・結果の案内を、読取りのたびに現在の表示言語で組み立てます。</summary>
internal static class AudioDuplicateOrganizePresentation
{
    /// <summary>結果の詳細に出す、完了以外のフォルダの最大件数です。超える分は「ほか{n}フォルダ…」に省略します。</summary>
    internal const int MaxDetailFolders = 20;

    /// <summary>
    /// 捕捉した対象から、モデルの要求を作ります。順は渡された順（一覧の表示順）で、各フォルダは捨てる形式と、確認が確定した衝突名のすべてを持ちます。
    /// 削除方法は、全対象で捕捉時点の同じ値を、操作全体で一つ持ちます。
    /// </summary>
    internal static AudioDuplicateOrganizeRequest CreateRequest(IReadOnlyList<AudioDuplicateCandidateTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return new AudioDuplicateOrganizeRequest(targets.Select(target => new AudioDuplicateOrganizeFolder(
            target.Folder,
            target.CollisionCleanup == AudioDuplicateCollisionCleanupMode.KeepOgg ? ".wav" : ".ogg",
            target.Inspection?.CollisionNames ?? [])), GetDeleteMethod(targets));
    }

    /// <summary>捕捉した対象の削除方法です。「削除」の値は全対象で捕捉時点の同じ値のため、先頭の対象から読みます。対象がなければ、既定のごみ箱へ移動です。</summary>
    internal static DuplicateOrganizeDeleteMethod GetDeleteMethod(IReadOnlyList<AudioDuplicateCandidateTarget> targets)
        => targets.Count == 0 ? DuplicateOrganizeDeleteMethod.MoveToRecycleBin : targets[0].DeleteMethod;

    /// <summary>進捗の「何番目／総数」とフォルダ名の文言です。</summary>
    internal static string CreateProgressMessage(AudioDuplicateOrganizeProgress progress)
        => string.Format(
            Resources.Duplicate_batch_organize_progress_group_format,
            progress.Number,
            progress.Total,
            DuplicateBatchOrganizePresentation.GetRepresentativeFolderName(progress.Folder));

    /// <summary>
    /// 確認文を作ります。通常の本文は、フォルダ数と対象の衝突名の合計の行と、残す形式の行です。残す形式の行は、削除方法がごみ箱へ移動なら「ごみ箱へ送ります」、完全に削除なら「完全に削除します」の文言です。
    /// 警告用の本文（警告色で表示する部分）は、削除方法が完全に削除のときだけ先頭に元に戻せない旨の行、
    /// 続いて、要注意のフォルダがあるときだけ、その件数の行と、同等と判定されていない組も一方だけが残る行です（このとき警告のアイコン）。
    /// </summary>
    /// <param name="hasWarning">要注意のフォルダを含む（続行のチェックを求める）場合はtrueです。完全に削除だけを理由にはtrueにしません（そのときのアイコンは呼出し側が警告にします）。</param>
    /// <param name="warningMessage">警告用の本文です。該当する行がなければnullです。</param>
    internal static string CreateConfirmationMessage(
        IReadOnlyList<AudioDuplicateCandidateTarget> targets, out bool hasWarning, out string warningMessage)
    {
        ArgumentNullException.ThrowIfNull(targets);
        int pairs = targets.Sum(target => target.Inspection?.CollisionNames.Count ?? 0);
        bool keepOgg = targets.Count == 0 || targets[0].CollisionCleanup == AudioDuplicateCollisionCleanupMode.KeepOgg;
        bool permanent = GetDeleteMethod(targets) == DuplicateOrganizeDeleteMethod.DeletePermanently;
        string message = string.Join(
            Environment.NewLine,
            string.Format(Resources.Audio_duplicate_organize_confirm_pairs_format, targets.Count, pairs),
            (keepOgg, permanent) switch
            {
                (true, false) => Resources.Audio_duplicate_organize_confirm_keep_ogg,
                (false, false) => Resources.Audio_duplicate_organize_confirm_keep_wav,
                (true, true) => Resources.Audio_duplicate_organize_confirm_keep_ogg_permanent,
                (false, true) => Resources.Audio_duplicate_organize_confirm_keep_wav_permanent
            });
        int caution = targets.Count(target => target.Inspection is { Stage: AudioDuplicateInspectionStage.Caution });
        hasWarning = caution > 0;
        var warnings = new List<string>();
        if (permanent) { warnings.Add(Resources.Duplicate_batch_organize_confirm_permanent_warning); }
        if (hasWarning)
        {
            warnings.Add(string.Format(Resources.Audio_duplicate_organize_confirm_caution_format, caution));
            warnings.Add(string.Format(Resources.Audio_duplicate_organize_confirm_non_equivalent_format, caution));
        }
        warningMessage = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
        return message;
    }

    /// <summary>
    /// 結果の案内を作ります。見出しは四区分（0件の区分も含む）の件数、削除した音源の合計が一件以上ならその行（文言は要求の削除方法に従い、ごみ箱へ送った／完全に削除した）、取消されていれば取消の行を続け、
    /// 完了以外のフォルダを要求順に最大 <see cref="MaxDetailFolders"/> 件まで「フォルダのパス：状態（理由）」で並べます。
    /// </summary>
    /// <param name="deleteMethod">整理の要求の削除方法です。削除の行の文言を決めます。</param>
    /// <param name="allCompleted">全フォルダが完了で、取消もない場合はtrueです。案内のアイコンの選択に使います。</param>
    internal static string CreateResultMessage(AudioDuplicateOrganizeResult result, DuplicateOrganizeDeleteMethod deleteMethod, out bool allCompleted)
    {
        ArgumentNullException.ThrowIfNull(result);
        IReadOnlyList<AudioDuplicateOrganizeFolderResult> folders = result.Folders;
        int Count(AudioDuplicateOrganizeStatus status) => folders.Count(folder => folder.Status == status);
        var lines = new List<string>
        {
            string.Format(
                Resources.Audio_duplicate_organize_result_format,
                folders.Count,
                Count(AudioDuplicateOrganizeStatus.Completed),
                Count(AudioDuplicateOrganizeStatus.PartiallyCompleted),
                Count(AudioDuplicateOrganizeStatus.Skipped),
                Count(AudioDuplicateOrganizeStatus.Unprocessed))
        };
        int deleted = folders.Sum(folder => folder.DeletedCount);
        if (deleted > 0)
        {
            lines.Add(string.Format(
                deleteMethod == DuplicateOrganizeDeleteMethod.DeletePermanently
                    ? Resources.Audio_duplicate_organize_result_deleted_format
                    : Resources.Audio_duplicate_organize_result_trashed_format,
                deleted));
        }
        if (result.Canceled) { lines.Add(Resources.Duplicate_batch_organize_result_canceled); }

        AudioDuplicateOrganizeFolderResult[] incomplete = [.. folders.Where(folder => folder.Status != AudioDuplicateOrganizeStatus.Completed)];
        if (incomplete.Length > 0) { lines.Add(string.Empty); }
        foreach (AudioDuplicateOrganizeFolderResult folder in incomplete.Take(MaxDetailFolders))
        {
            string status = StatusText(folder.Status);
            string reason = folder.Status switch
            {
                AudioDuplicateOrganizeStatus.Skipped => DuplicateBatchOrganizePresentation.SkipReasonText(folder.SkipReason),
                AudioDuplicateOrganizeStatus.PartiallyCompleted => Resources.Duplicate_batch_organize_reason_delete_failure,
                _ => null
            };
            lines.Add(reason == null
                ? string.Format(Resources.Duplicate_batch_organize_result_group_format, folder.Folder, status)
                : string.Format(Resources.Duplicate_batch_organize_result_group_reason_format, folder.Folder, status, reason));
        }
        if (incomplete.Length > MaxDetailFolders)
        {
            lines.Add(string.Format(Resources.Audio_duplicate_organize_result_more_format, incomplete.Length - MaxDetailFolders));
        }
        allCompleted = incomplete.Length == 0 && !result.Canceled;
        return string.Join(Environment.NewLine, lines);
    }

    private static string StatusText(AudioDuplicateOrganizeStatus status) => status switch
    {
        AudioDuplicateOrganizeStatus.Completed => Resources.Duplicate_batch_organize_status_completed,
        AudioDuplicateOrganizeStatus.PartiallyCompleted => Resources.Duplicate_batch_organize_status_partial,
        AudioDuplicateOrganizeStatus.Skipped => Resources.Duplicate_batch_organize_status_skipped,
        AudioDuplicateOrganizeStatus.Unprocessed => Resources.Duplicate_batch_organize_status_unprocessed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown audio duplicate organize status.")
    };
}
