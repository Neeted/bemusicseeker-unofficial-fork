using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>統合候補一覧からの一括整理の結末です。</summary>
internal enum DuplicateBatchOrganizeRunStatus
{
    /// <summary>対象のグループが0件で、受付・確認・案内・進捗なしに終えました。</summary>
    NoTargets,

    /// <summary>
    /// 分裂配置の一覧からの整理で、対象に未確認のグループがあったため、先に安全性確認を行う案内を一件だけ出して終えました。
    /// 受付の取得・確認・再生停止・モデルの呼出し・探索を行わず、状態を変えていません。
    /// </summary>
    UnverifiedTargets,

    /// <summary>共通のライブラリ変更受付が使用中で取得できず、Busyを案内して状態を変えずに終えました。</summary>
    Busy,

    /// <summary>共通のライブラリ変更受付が閉じている（終了の要求中など）ため、案内なしに状態を変えずに終えました。</summary>
    NotStarted,

    /// <summary>確認でキャンセルまたは閉じられ、状態を変えずに終えました。</summary>
    Declined,

    /// <summary>確認の表示失敗、再生停止の失敗、モデルの例外など、モデルの結果を得られずに（または得られても結果の案内の前に）終えました。失敗は一回だけ報告済みです。</summary>
    Failed,

    /// <summary>モデルが操作全体を受理しませんでした（Busy・カタログ未収束・管理出力の保護）。モデルが積んだ案内だけを表示済みで、結果は案内しません。</summary>
    NotAdmitted,

    /// <summary>既存宛先を復元できず、報告と結果の案内なしに終了処理へ接続しました。</summary>
    UnrestoredDestination,

    /// <summary>モデルの結果を得て、結果を一回案内しました。取消・一部の失敗を含みます。</summary>
    Completed
}

/// <summary>統合候補一覧からの一括整理の結果です。</summary>
/// <param name="Status">結末。</param>
/// <param name="Result">モデルの結果。得られなかった結末ではnullです。</param>
/// <param name="Failure">報告した失敗。ないときはnullです。</param>
internal sealed record DuplicateBatchOrganizeRunResult(
    DuplicateBatchOrganizeRunStatus Status, DuplicateBatchOrganizeResult Result = null, Exception Failure = null)
{
    internal static DuplicateBatchOrganizeRunResult NoTargets { get; } = new(DuplicateBatchOrganizeRunStatus.NoTargets);

    internal static DuplicateBatchOrganizeRunResult UnverifiedTargets { get; } = new(DuplicateBatchOrganizeRunStatus.UnverifiedTargets);

    internal static DuplicateBatchOrganizeRunResult Busy { get; } = new(DuplicateBatchOrganizeRunStatus.Busy);

    internal static DuplicateBatchOrganizeRunResult NotStarted { get; } = new(DuplicateBatchOrganizeRunStatus.NotStarted);

    internal static DuplicateBatchOrganizeRunResult Declined { get; } = new(DuplicateBatchOrganizeRunStatus.Declined);
}

/// <summary>
/// 統合候補一覧の「選択グループを整理」の手順を管理します。表示中でSELECTがオンのグループを一つの要求にまとめ、共通のライブラリ変更受付を非待機で取得して、
/// 常に一度だけ確認し、承認後は再生の停止、画面更新の抑制、モデルの一括整理（UIスレッド外）を、取消できる進捗画面の内側で行います。
/// </summary>
/// <remarks>
/// 受付は、モデルの終端・重複結果の準備・画面更新の抑制の解除まで保持し、結果の案内より前に解放します。
/// 確定・反映・保守の失敗は単体の統合と同じ報告を一度出し、その後に結果を一度だけ案内します。
/// 取消は、処理中のグループを終えてから止まり、反映段階へ入った後は取消できません（進捗画面は取消の扱いを操作へ任せます）。
/// モデルが操作を受理して結果を返したら、対象のうち「未処理」以外のグループのSELECTを、整理を始めた一覧でオフにします（SELECTは整理で消費されます）。既存宛先を復元できなかった場合は終了手順へ進み、SELECTの扱いは規定しません。
/// </remarks>
internal sealed partial class DuplicateMaintenanceWorkflowOwner
{
    /// <summary>進捗画面の取消の要求を、モデルの取消の合図へ伝える間隔です。</summary>
    private static readonly TimeSpan BatchCancellationPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 重複の統合候補一覧のツールバーの「選択グループを整理」です。対象が0件なら、受付・確認・案内・再生停止・進捗・モデルの呼出しのどれも行いません。
    /// 未確認のグループも、確認ダイアログの承認で対象にします。整理の後は重複結果を明示的に準備し直します。
    /// </summary>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal Task<DuplicateBatchOrganizeRunResult> OrganizeSelectedGroupsAsync(System.Windows.Window owner = null)
        => OrganizeCandidateListAsync(
            candidateList
                ?? throw new InvalidOperationException("The duplicate merge candidate list is not connected to the duplicate maintenance workflow."),
            splitPlacement: false,
            owner);

    /// <summary>
    /// 分裂配置の統合候補一覧のツールバーの「選択グループを整理」です。対象が0件なら何もしません。
    /// 対象に未確認のグループが一つでもあれば、受付の取得より前に、先に安全性確認を行う案内を一件だけ出して終わります。
    /// 整理の後に有効な分裂配置の結果がなければ、同じ受付の中で取消の対象にせず探索し直し、重複結果は明示的に準備しません。
    /// </summary>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal Task<DuplicateBatchOrganizeRunResult> OrganizeSelectedSplitPlacementGroupsAsync(System.Windows.Window owner = null)
        => OrganizeCandidateListAsync(
            splitPlacementCandidateList
                ?? throw new InvalidOperationException("The split placement candidate list is not connected to the duplicate maintenance workflow."),
            splitPlacement: true,
            owner);

    /// <summary>重複と分裂配置の一覧からの一括整理が共有する手順です。違いは、対象の一覧主体、未確認の拒否、整理後の再探索と重複結果の準備だけです。</summary>
    private async Task<DuplicateBatchOrganizeRunResult> OrganizeCandidateListAsync(
        DuplicateMergeCandidateListOwner list, bool splitPlacement, System.Windows.Window owner)
    {
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets = list.CaptureSelectedGroups();
        if (targets.Count == 0)
        {
            return DuplicateBatchOrganizeRunResult.NoTargets;
        }
        if (splitPlacement
            && targets.Any(target => (target.Verification ?? DuplicateMergeVerificationResult.Unverified).Stage == DuplicateMergeVerificationStage.Unverified))
        {
            // 状態を変えず、受付も取らずに案内だけで終える。
            try
            {
                UiDialogResult notice = await dialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(
                    Resources.Split_placement_batch_organize_unverified_notice, Resources.Warning));
                UiDialogRoute.ThrowIfNotShown(notice, "Split placement batch organize unverified notification");
                return DuplicateBatchOrganizeRunResult.UnverifiedTargets;
            }
            catch (Exception exception)
            {
                await ReportBatchFailureAsync(exception);
                return new DuplicateBatchOrganizeRunResult(DuplicateBatchOrganizeRunStatus.Failed, Failure: exception);
            }
        }
        if (!chartFileOperations.TryEnter(out IDisposable operationGate))
        {
            // 受付の閉鎖（終了の要求中など）による不成立は案内しません。他の受付を使う手順と同じ扱いです。
            if (chartFileOperations.IsAdmissionClosed)
            {
                return DuplicateBatchOrganizeRunResult.NotStarted;
            }
            try
            {
                await ShowBusyWarningAsync();
                return DuplicateBatchOrganizeRunResult.Busy;
            }
            catch (Exception exception)
            {
                await ReportBatchFailureAsync(exception);
                return new DuplicateBatchOrganizeRunResult(DuplicateBatchOrganizeRunStatus.Failed, Failure: exception);
            }
        }

        BatchRun run = null;
        DuplicateBatchOrganizeRunResult early = null;
        DuplicateMaintenanceMutationResult mutation = null;
        Exception routeFailure = null;
        try
        {
            // 要求の作成が例外で終わっても、下の finally が受付を解放します。
            run = new BatchRun(DuplicateBatchOrganizePresentation.CreateRequest(targets), splitPlacement, list, targets);
            ConfirmationDecision confirmation = await ConfirmBatchOrganizeAsync(targets);
            if (confirmation.Failure != null)
            {
                early = new DuplicateBatchOrganizeRunResult(DuplicateBatchOrganizeRunStatus.Failed, Failure: confirmation.Failure);
            }
            else if (!confirmation.Accepted)
            {
                early = DuplicateBatchOrganizeRunResult.Declined;
            }
            else
            {
                routeFailure = await RunBatchOrganizeProgressAsync(run, operationGate, owner);
                if (run.Execution != null)
                {
                    try { mutation = await run.Execution; }
                    catch (Exception exception) { routeFailure ??= exception; }
                }
            }
        }
        finally
        {
            // 変更の実行を開始しなかった（確認の拒否・失敗、進捗画面が操作を呼ばなかった）場合だけ、ここで受付を解放します。
            // 実行を開始した場合は、モデルの終端・重複結果の準備・画面更新の抑制の解除の後に、実行側が解放します。
            if (run == null || !run.GateTransferred)
            {
                operationGate.Dispose();
            }
        }

        if (early != null)
        {
            if (early.Failure != null)
            {
                await ReportBatchFailureAsync(early.Failure);
            }
            return early;
        }
        return await CompleteBatchOrganizeAsync(run, mutation, routeFailure);
    }

    /// <summary>
    /// 一括整理の確認を表示します。確認を表示しない設定（<c>ShowDuplicateFileCheckConfirmMsg</c>）でも、常に一度表示します。
    /// OKとキャンセルがあり、既定はキャンセルです。未確認または要注意のグループを含むときだけ、警告アイコンを表示し、チェックを入れるまでOKを無効にします。
    /// 未確認・要注意と内容が異なる同名のファイルの注意は、警告用の本文（警告色の部分）に表示します。
    /// </summary>
    private async Task<ConfirmationDecision> ConfirmBatchOrganizeAsync(IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets)
    {
        try
        {
            string message = DuplicateBatchOrganizePresentation.CreateConfirmationMessage(targets, out bool hasWarning, out string warningMessage);
            return await ConfirmAsync(
                new UiConfirmationRequest(
                    message,
                    Resources.Confirm,
                    MessageBoxButton.OKCancel,
                    hasWarning ? MessageBoxImage.Warning : MessageBoxImage.Question,
                    MessageBoxResult.Cancel,
                    warningMessageBoxText: warningMessage)
                {
                    // 利用者の辞書が空でも、選択対象が必要とする確認を省略しません。
                    AcknowledgmentText = hasWarning ? Resources.Duplicate_batch_organize_acknowledgment ?? string.Empty : null
                },
                "Duplicate batch organize confirmation");
        }
        catch (Exception exception)
        {
            return ConfirmationDecision.Failed(exception);
        }
    }

    /// <summary>
    /// 進捗画面を開き、その操作の内側で変更を実行します。画面の経路が失敗した場合の例外を返します（成功と取消はnullです）。
    /// 画面が操作を呼ばなかった場合は、受付が未譲渡のまま、呼出し側が解放できるよう失敗として返します。
    /// </summary>
    private async Task<Exception> RunBatchOrganizeProgressAsync(BatchRun run, IDisposable operationGate, System.Windows.Window owner)
    {
        try
        {
            UiProgressResult progressResult = await dialogs.RunWithProgressAsync(
                new UiProgressRequest(
                    Resources.Duplicate_batch_organize_title,
                    Resources.Duplicate_batch_organize_progress_label,
                    new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false)
                    {
                        // 反映段階では取消できず、取消の要求中でも操作の例外と結果を失わないよう、取消の扱いを操作へ任せます。
                        OperationOwnsCancellation = true
                    },
                    owner),
                context => RunBatchOrganizeOperationAsync(run, context, operationGate));
            if (progressResult == null)
            {
                return new InvalidOperationException("Duplicate batch organize progress route returned no result.");
            }
            // 進捗の仕組みが取消を返しても、結果の案内と報告は省きません。実行の終端は呼出し側が待ちます。
            if (progressResult.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
            {
                return run.Execution == null
                    ? new InvalidOperationException("Duplicate batch organize progress route did not run the operation.")
                    : null;
            }
            return new InvalidOperationException(
                "Duplicate batch organize progress route failed: " + progressResult.Status, progressResult.Error);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// 進捗画面の操作です。受付を実行側へ譲り、UIスレッド外で、再生の停止・画面更新の抑制・モデルの一括整理・重複結果の準備を行います。
    /// 取消ボタンの押下は、反映段階の前だけ、モデルの取消の合図へ伝えます。実行の終端まで画面を閉じません。
    /// </summary>
    private async Task RunBatchOrganizeOperationAsync(BatchRun run, UiProgressContext context, IDisposable operationGate)
    {
        using var cancellation = new CancellationTokenSource();
        Task<DuplicateMaintenanceMutationResult> execution = Task.Run(() => ExecuteMutation(
            selectionContext: null,
            mutation: null,
            mutationWithReceipt: (library, operationStore, notices) =>
            {
                DuplicateBatchOrganizeResult result = operationStore.OrganizeDuplicateGroups(
                    library,
                    run.Request,
                    Stopwatch.GetTimestamp(),
                    notices,
                    progress => ReportBatchOrganizeProgress(run, context, progress),
                    cancellation.Token);
                Volatile.Write(ref run.ModelResult, result);
                return result.Receipt;
            },
            acquiredOperationGate: operationGate,
            physicalPaths: null,
            // 分裂配置の一覧からの整理は、重複の一覧を表示していないため、重複結果を明示的に準備しない（他のライブラリ変更と同じ扱い）。
            requireDuplicateGroups: !run.SplitPlacement,
            // 受理した変更の後に有効な分裂配置の結果がなければ（世代だけが古い場合を含む）、同じ受付と画面更新の抑制の内側で、取消の対象にせず探索し直す。
            followUp: run.SplitPlacement
                ? (library, operationStore) =>
                {
                    SplitPlacementSearchCriteria criteria = splitPlacementCriteriaProvider();
                    if (!library.HasCurrentSplitPlacementGroups(criteria)) { operationStore.SearchSplitPlacementGroups(library, criteria); }
                }
        : null));
        // 実行を開始できた後にだけ、受付の解放を実行側へ譲ります（開始できなければ呼出し側が解放します）。
        Volatile.Write(ref run.Execution, execution);
        run.GateTransferred = true;
        while (!execution.IsCompleted)
        {
            if (!Volatile.Read(ref run.Applying) && context.CancellationRequested)
            {
                cancellation.Cancel();
            }
            PostLatestBatchOrganizeProgress(run, context);
            await Task.WhenAny(execution, Task.Delay(BatchCancellationPollInterval)).ConfigureAwait(false);
        }
        // 最後の値（反映段階を含む）を必ず一度投稿します。
        PostLatestBatchOrganizeProgress(run, context);
        // 結果と例外は、呼出し側が実行のTaskから読みます。画面の操作としては、終端まで待つだけです。
        try { await execution.ConfigureAwait(false); }
        catch { /* 呼出し側が同じTaskから失敗を読みます。 */ }
    }

    /// <summary>
    /// モデルの進捗を受け取ります。待たずに戻り、最新の値だけを保持して、画面への投稿は <see cref="PostLatestBatchOrganizeProgress"/> が間引きます。
    /// 番号は後退せず、反映段階への移行は以後のグループ開始の値に上書きされません。反映段階では取消できないため、取消ボタンはここですぐ無効にします。
    /// </summary>
    private static void ReportBatchOrganizeProgress(BatchRun run, UiProgressContext context, DuplicateBatchOrganizeProgress progress)
    {
        if (progress.Kind == DuplicateBatchOrganizeProgressKind.Applying)
        {
            Volatile.Write(ref run.Applying, true);
            context.DisableCancellation();
            return;
        }
        lock (run)
        {
            if (run.Latest is { } latest && progress.Number < latest.Number)
            {
                return;
            }
            run.Latest = progress;
        }
    }

    /// <summary>
    /// 保持している最新の値を、前回投稿した値と異なる場合だけ画面へ投稿します。投稿は呼出し側の一つの流れだけが行うため、順序は逆転しません。
    /// 反映段階に入った後は、グループ開始の値より後に必ず「ライブラリへ反映中」を投稿します。
    /// </summary>
    private static void PostLatestBatchOrganizeProgress(BatchRun run, UiProgressContext context)
    {
        int percent;
        string message;
        if (Volatile.Read(ref run.Applying))
        {
            percent = 100;
            message = Resources.Duplicate_batch_organize_progress_applying;
        }
        else
        {
            DuplicateBatchOrganizeProgress latest;
            lock (run)
            {
                if (run.Latest is not { } value) { return; }
                latest = value;
            }
            percent = 100 * Math.Max(latest.Number - 1, 0) / Math.Max(latest.Total, 1);
            message = DuplicateBatchOrganizePresentation.CreateGroupProgressMessage(latest);
        }
        if (run.Posted == (percent, message))
        {
            return;
        }
        run.Posted = (percent, message);
        context.Report(percent, message);
    }

    /// <summary>
    /// 実行の終端の後の報告です。既存宛先を復元できなかった場合は報告と結果の案内なしに終了処理へ接続し、
    /// 確定・反映・保守の失敗は単体の統合と同じ報告を一度出し、モデルが受理した結果は最後に一度だけ案内します。
    /// </summary>
    private async Task<DuplicateBatchOrganizeRunResult> CompleteBatchOrganizeAsync(
        BatchRun run, DuplicateMaintenanceMutationResult mutation, Exception routeFailure)
    {
        DuplicateBatchOrganizeResult model = Volatile.Read(ref run.ModelResult);
        LibraryMutationSessionReceipt session = model?.Receipt.SessionReceipt ?? LibraryMutationSessionReceipt.Empty;
        Exception failure = CombineFailures(GetReportableFailure(mutation, session), routeFailure);
        if (session.HasUnrestoredExistingDestination)
        {
            await exitForUnrestoredDestination(session.PrimaryFailure);
            return new DuplicateBatchOrganizeRunResult(DuplicateBatchOrganizeRunStatus.UnrestoredDestination, model, failure);
        }
        // 移動元ごとの事前拒否（型の衝突など）は結果の理由として案内するため、ここでは報告しません。
        if (failure != null
            || session.PhysicalFailure != null
            || session.ApplyFailure != null
            || session.FinalizationFailure != null
            || session.CleanupFailure != null
            || session.ManualRecoveryRequired)
        {
            await FileDbMutationReport.ShowAsync(
                dialogs, Resources.Duplicate_batch_organize_title, session, failure, mergeOperation: true);
        }
        if (model == null)
        {
            return new DuplicateBatchOrganizeRunResult(DuplicateBatchOrganizeRunStatus.Failed, null, failure);
        }
        if (!model.Admitted)
        {
            return new DuplicateBatchOrganizeRunResult(DuplicateBatchOrganizeRunStatus.NotAdmitted, model, failure);
        }
        ConsumeSelectOfProcessedGroups(run, model);
        await ShowBatchOrganizeResultAsync(model);
        return new DuplicateBatchOrganizeRunResult(DuplicateBatchOrganizeRunStatus.Completed, model, failure);
    }

    /// <summary>
    /// 報告する失敗を返します。モデルの受取りが持つ失敗が移動元ごとの事前拒否だけに由来する場合は、結果の理由として案内するため、報告しません。
    /// </summary>
    private static Exception GetReportableFailure(DuplicateMaintenanceMutationResult mutation, LibraryMutationSessionReceipt session)
    {
        Exception failure = mutation?.Failure;
        if (failure == null)
        {
            return null;
        }
        bool onlyItemRefusals = ReferenceEquals(failure, session.PrimaryFailure)
            && session.PhysicalFailure == null
            && session.ApplyFailure == null
            && session.FinalizationFailure == null
            && session.CleanupFailure == null
            && !session.ManualRecoveryRequired;
        return onlyItemRefusals ? null : failure;
    }

    private static Exception CombineFailures(Exception first, Exception second)
        => first == null ? second : second == null ? first : new AggregateException(first, second);

    /// <summary>
    /// モデルが操作を受理して結果を返したとき、対象のグループのうち「未処理」以外（完了・一部完了・スキップ・失敗）のSELECTを、整理を始めた一覧でオフにします。
    /// SELECTは整理で消費されます。取消や停止で着手しなかった「未処理」のグループは変えず、KEEPと確認結果も書き換えません。UIスレッドで呼びます。既存宛先を復元できなかった経路では呼びません。
    /// 一覧の状態表にないグループ（整理で解消し、作り直しで消えたものなど）の書込みは捨てます。
    /// </summary>
    private static void ConsumeSelectOfProcessedGroups(BatchRun run, DuplicateBatchOrganizeResult model)
    {
        int count = Math.Min(run.Targets.Count, model.Groups.Count);
        var processed = new List<DuplicateGroup>(count);
        for (int index = 0; index < count; index++)
        {
            if (model.Groups[index].Status != DuplicateBatchOrganizeGroupStatus.Unprocessed) { processed.Add(run.Targets[index].Group); }
        }
        run.List.ClearSelectOfGroups(processed);
    }

    /// <summary>結果を一度だけ案内します。表示の失敗は診断へ残すだけで、整理の結果を変えません。</summary>
    private async Task ShowBatchOrganizeResultAsync(DuplicateBatchOrganizeResult model)
    {
        try
        {
            string message = DuplicateBatchOrganizePresentation.CreateResultMessage(model, out bool allCompleted);
            UiDialogResult result = await dialogs.ShowMessageAsync(UiMessageRequest.CreateInformation(
                message, Resources.Duplicate_batch_organize_title, completedSuccessfully: allCompleted));
            UiDialogRoute.ThrowIfNotShown(result, "Duplicate batch organize result notification");
        }
        catch (Exception exception)
        {
            FileDbMutationReport.LogNotificationFailure(exception);
        }
    }

    /// <summary>確認以外で終わった失敗を、単体の統合と同じ報告で一度だけ表示します。表示の失敗は診断へ残すだけです。</summary>
    private Task ReportBatchFailureAsync(Exception failure)
        => FileDbMutationReport.ShowAsync(
            dialogs, Resources.Duplicate_batch_organize_title, LibraryMutationSessionReceipt.Empty, failure, mergeOperation: true);

    /// <summary>一回の一括整理の実行状態です。進捗画面の別スレッドと呼出し側が共有するため、読み書きは明示的に行います。</summary>
    private sealed class BatchRun(
        DuplicateBatchOrganizeRequest request,
        bool splitPlacement,
        DuplicateMergeCandidateListOwner list,
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets)
    {
        internal DuplicateBatchOrganizeRequest Request { get; } = request;

        /// <summary>整理を始めた一覧の管理主体です。整理で処理したグループのSELECTをオフにする書込み先で、画面に出ているかによりません。</summary>
        internal DuplicateMergeCandidateListOwner List { get; } = list;

        /// <summary>整理を始めたときに捕捉した対象です。モデルの結果のグループと同じ順です。</summary>
        internal IReadOnlyList<DuplicateMergeCandidateGroupTarget> Targets { get; } = targets;

        /// <summary>分裂配置の一覧からの整理かです。整理後の再探索と、重複結果の準備の要否を決めます。</summary>
        internal bool SplitPlacement { get; } = splitPlacement;

        /// <summary>変更の実行のTaskです。進捗画面が操作を呼んだ後にだけ設定されます。</summary>
        internal Task<DuplicateMaintenanceMutationResult> Execution;

        /// <summary>モデルの結果です。モデルが終端していない（停止の失敗・モデルの例外）場合はnullです。</summary>
        internal DuplicateBatchOrganizeResult ModelResult;

        /// <summary>受付を実行側へ譲り渡したかです。譲渡後の解放は実行側が行います。</summary>
        internal bool GateTransferred;

        /// <summary>モデルが反映段階へ入ったかです。以後は取消の合図を送りません。</summary>
        internal bool Applying;

        /// <summary>モデルが通知した最新のグループ開始の値です。<c>lock (this)</c> で読み書きします。</summary>
        internal DuplicateBatchOrganizeProgress? Latest;

        /// <summary>画面へ最後に投稿した値です。投稿する流れだけが読み書きします。</summary>
        internal (int Percent, string Message)? Posted;
    }
}
