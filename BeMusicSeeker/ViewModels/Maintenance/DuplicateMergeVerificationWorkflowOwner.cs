using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>統合候補一覧の安全性確認の結末です。</summary>
internal enum DuplicateMergeVerificationRunStatus
{
    /// <summary>対象のグループが0件で、受付・進捗・案内なしに終えました。</summary>
    NoTargets,
    /// <summary>共通のライブラリ変更受付が使用中で取得できず、Busyを案内して状態を変えずに終えました。</summary>
    Busy,
    /// <summary>共通のライブラリ変更受付が閉じている（終了の要求中など）ため、案内なしに状態を変えずに終えました。</summary>
    NotStarted,
    /// <summary>すべての対象を確認しました。</summary>
    Completed,
    /// <summary>取消により、着手しなかったグループがあります。</summary>
    Cancelled
}

/// <summary>統合候補一覧の安全性確認の結果です。件数は、状態表へ書き込んだグループだけを数え、確認中に状態表から消えて捨てたグループを含みません。</summary>
/// <param name="Status">結末。</param>
/// <param name="Checked">確認して書き込んだグループ数。</param>
/// <param name="Safe">安全のグループ数。</param>
/// <param name="Caution">要注意のグループ数。</param>
/// <param name="Unmergeable">統合不可のグループ数。</param>
/// <param name="Unverified">取消で着手せず、確認前のままのグループ数。</param>
internal sealed record DuplicateMergeVerificationRunResult(
    DuplicateMergeVerificationRunStatus Status, int Checked, int Safe, int Caution, int Unmergeable, int Unverified)
{
    internal static DuplicateMergeVerificationRunResult NoTargets { get; } = new(DuplicateMergeVerificationRunStatus.NoTargets, 0, 0, 0, 0, 0);

    internal static DuplicateMergeVerificationRunResult Busy { get; } = new(DuplicateMergeVerificationRunStatus.Busy, 0, 0, 0, 0, 0);

    internal static DuplicateMergeVerificationRunResult NotStarted { get; } = new(DuplicateMergeVerificationRunStatus.NotStarted, 0, 0, 0, 0, 0);
}

/// <summary>
/// 統合候補一覧の安全性確認の手順を管理します。確認は読み取りだけで、共通のライブラリ変更受付を非待機で取得して開始から結果の書込みまで保持し、
/// モーダルの進捗の下でグループごとに逐次、UIスレッド外で走査します。結果の書込みは一括で行い、受付を解放してから結果を一度だけ案内します。
/// </summary>
/// <remarks>
/// 受付が使用中ならBusyを一度だけ案内し、受付が閉じている（終了の要求中など）なら案内せずに、どちらも状態を変えずに終えます。
/// 取消は、処理中のグループを終えてから止まります。着手しなかったグループの確認結果とSELECTは変えません。
/// 確認を終えたグループのSELECTは、段階によらずオフにします（確認で消費し、安全でも自動ではオンにしません）。
/// 入出力以外の予期しない例外は、完了済みのグループの結果を書き込み、受付を解放した後に呼出元へ伝えます。
/// 状態表の読み書きはUIスレッドで行い、走査の間はモデルのロックもDBトランザクションも保持しません。
/// </remarks>
internal sealed class DuplicateMergeVerificationWorkflowOwner
{
    /// <summary>実行側の進捗を画面へ渡す間隔です。最新の位置だけを間引いて渡します。</summary>
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly DuplicateMergeCandidateListOwner list;
    private readonly Func<IDuplicateMergeSafetyInspector> inspectorFactory;
    private readonly ChartFileOperationSynchronizer operationAdmission;
    private readonly IUiDialogService dialogs;

    /// <param name="list">一覧の管理主体。対象の捕捉と結果の書込みに使います。</param>
    /// <param name="inspectorFactory">確認ごとに一回だけ呼び、現在のライブラリの検査器を返します。</param>
    /// <param name="operationAdmission">共通のライブラリ変更受付。</param>
    /// <param name="dialogs">進捗と案内の窓口。</param>
    internal DuplicateMergeVerificationWorkflowOwner(
        DuplicateMergeCandidateListOwner list,
        Func<IDuplicateMergeSafetyInspector> inspectorFactory,
        ChartFileOperationSynchronizer operationAdmission,
        IUiDialogService dialogs)
    {
        this.list = list ?? throw new ArgumentNullException(nameof(list));
        this.inspectorFactory = inspectorFactory ?? throw new ArgumentNullException(nameof(inspectorFactory));
        this.operationAdmission = operationAdmission ?? throw new ArgumentNullException(nameof(operationAdmission));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <summary>ツールバーの「選択グループを確認」です。表示中でSELECTがオンのグループを確認します。</summary>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal Task<DuplicateMergeVerificationRunResult> VerifySelectedAsync(Window owner = null)
        => RunAsync(list.CaptureSelectedGroups(), owner);

    /// <summary>右クリックメニューの「このグループを確認」です。行が属するグループを、グループごとに一度、SELECTの状態によらず確認します。</summary>
    /// <param name="rows">選択行。</param>
    /// <param name="owner">進捗画面の所有者です。メインウィンドウを渡します。</param>
    internal Task<DuplicateMergeVerificationRunResult> VerifyGroupsOfRowsAsync(IEnumerable<DuplicateMergeCandidateRow> rows, Window owner = null)
        => RunAsync(list.CaptureGroupsOfRows(rows), owner);

    /// <summary>
    /// 「サイズと更新日時が同じファイルは読まずに同一とみなす」のチェックの変更です。値は一覧の管理主体が保持します。
    /// オンにするたびに、内容が異なるファイルを同一と判定することがある旨を一度だけ案内し、オフにするときは案内しません。
    /// </summary>
    internal async Task SetQuickCompareAsync(bool enabled)
    {
        list.QuickCompareEnabled = enabled;
        if (enabled)
        {
            await ShowOptionalNotificationAsync(
                UiMessageRequest.CreateWarning(Resources.Duplicate_merge_candidate_quick_compare_notice, Resources.Warning),
                "Duplicate merge quick compare notification");
        }
    }

    /// <summary>確認の実行状態です。走査の別スレッドとUIスレッドが共有するため、読み書きは明示的に行います。</summary>
    private sealed class Run(
        IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets,
        IReadOnlyList<DuplicateMergeCandidateGroupFolders> allGroups,
        bool sizeAndTimeOnly,
        IDuplicateMergeSafetyInspector inspector)
    {
        internal IReadOnlyList<DuplicateMergeCandidateGroupTarget> Targets { get; } = targets;
        internal IReadOnlyList<DuplicateMergeCandidateGroupFolders> AllGroups { get; } = allGroups;
        internal bool SizeAndTimeOnly { get; } = sizeAndTimeOnly;
        internal IDuplicateMergeSafetyInspector Inspector { get; } = inspector;

        internal Task ScanTask;
        internal int CurrentIndex;
        internal int CompletedCount;
        internal int CancelRequestedFlag;
        internal Exception Failure;

        /// <summary>走査を終えたグループです。走査の完了後だけUIスレッドが読みます。</summary>
        internal List<(DuplicateMergeCandidateGroupTarget Target, DuplicateMergeGroupInspection Inspection)> Completed { get; } = [];

        internal bool CancelRequested => Volatile.Read(ref CancelRequestedFlag) != 0;

        internal void RequestCancel() => Volatile.Write(ref CancelRequestedFlag, 1);
    }

    private async Task<DuplicateMergeVerificationRunResult> RunAsync(IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets, Window owner)
    {
        if (targets.Count == 0) { return DuplicateMergeVerificationRunResult.NoTargets; }
        if (!operationAdmission.TryEnter(out IDisposable lease))
        {
            // 受付の閉鎖（終了の要求中など）による不成立は案内しない。他の受付を使う手順と同じ扱いにする。
            if (operationAdmission.IsAdmissionClosed) { return DuplicateMergeVerificationRunResult.NotStarted; }
            await ShowOptionalNotificationAsync(
                UiMessageRequest.CreateWarning(Resources.Warn_LibraryOperationBusy, Resources.Warning),
                "Duplicate merge verification Busy notification");
            return DuplicateMergeVerificationRunResult.Busy;
        }

        Exception failure = null;
        DuplicateMergeVerificationRunResult result;
        bool cancelled;
        try
        {
            Run run = null;
            try
            {
                run = new Run(
                    targets,
                    list.CaptureAllGroupFolders(),
                    list.QuickCompareEnabled,
                    inspectorFactory() ?? throw new InvalidOperationException("Duplicate merge safety inspector was not provided."));
                UiProgressResult progressResult = await dialogs.RunWithProgressAsync(
                    new UiProgressRequest(
                        Resources.Duplicate_merge_candidate_verify_progress_title,
                        Resources.Duplicate_merge_candidate_verify_progress_label,
                        new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false),
                        owner),
                    context =>
                    {
                        Task operation = RunProgressOperationAsync(run, context);
                        return operation;
                    });
                if (progressResult == null)
                {
                    failure = new InvalidOperationException("Duplicate merge verification progress route returned no result.");
                }
                else if (progressResult.Status == UiDialogStatus.CancelledByUser)
                {
                    run.RequestCancel();
                }
                else if (progressResult.Status != UiDialogStatus.Accepted)
                {
                    failure = new InvalidOperationException(
                        "Duplicate merge verification progress route failed: " + progressResult.Status, progressResult.Error);
                }
                else if (run.ScanTask == null)
                {
                    failure = new InvalidOperationException("Duplicate merge verification progress route did not run the operation.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            // 走査の終端まで待つ。進捗が失敗や取消で終わっても、処理中のグループは最後まで処理する。
            if (run?.ScanTask != null)
            {
                if (failure != null) { run.RequestCancel(); }
                try { await run.ScanTask; }
                catch (Exception exception) { failure ??= exception; }
                failure ??= run.Failure;
            }

            (result, cancelled) = WriteResults(run, targets);
        }
        finally
        {
            lease.Dispose();
        }

        if (failure != null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        if (result.Checked > 0)
        {
            await ShowOptionalNotificationAsync(
                UiMessageRequest.CreateInformation(BuildResultMessage(result), Resources.Confirm, completedSuccessfully: !cancelled),
                "Duplicate merge verification result notification");
        }
        return result;
    }

    /// <summary>進捗画面の操作です。走査を開始し、終端まで進捗を表示します。</summary>
    private static async Task RunProgressOperationAsync(Run run, UiProgressContext context)
    {
        run.ScanTask = Task.Run(() => Scan(run), CancellationToken.None);
        int reported = -1;
        while (!run.ScanTask.IsCompleted)
        {
            try
            {
                int current = Volatile.Read(ref run.CurrentIndex);
                if (current != reported)
                {
                    reported = current;
                    int total = run.Targets.Count;
                    context.ReportWithCancellationCheck(
                        100 * Volatile.Read(ref run.CompletedCount) / total,
                        "[{0}/{1}] {2}",
                        Math.Min(current + 1, total),
                        total,
                        GetRepresentativeFolderName(run.Targets[Math.Min(current, total - 1)]));
                }
                else
                {
                    context.ThrowIfCancellationPending();
                }
            }
            catch (ProgressDialogCancellationExcpetion)
            {
                // 処理中のグループは最後まで処理して、画面を閉じる。
                run.RequestCancel();
                await Task.WhenAny(run.ScanTask);
                return;
            }
            await Task.WhenAny(run.ScanTask, Task.Delay(ProgressPollInterval));
        }
    }

    /// <summary>グループごとに逐次、走査します。UIスレッド外で動き、予期しない例外は走査を止めて記録します。</summary>
    private static void Scan(Run run)
    {
        try
        {
            var index = new DuplicateMergeFolderIndex(run.AllGroups.Select(group => ((object)group.Group, group.Folders)));
            for (int position = 0; position < run.Targets.Count; position++)
            {
                if (run.CancelRequested) { break; }
                Volatile.Write(ref run.CurrentIndex, position);
                DuplicateMergeCandidateGroupTarget target = run.Targets[position];
                DuplicateMergeGroupInspection inspection = run.Inspector.Inspect(
                    new DuplicateMergeInspectionRequest(target.Folders, target.RegisteredFolders, index, target.Group, run.SizeAndTimeOnly));
                run.Completed.Add((target, inspection));
                Volatile.Write(ref run.CompletedCount, position + 1);
            }
        }
        catch (Exception exception)
        {
            run.Failure = exception;
        }
    }

    /// <summary>進捗に出す代表フォルダ名は、グループのKEEPのフォルダ名です。</summary>
    private static string GetRepresentativeFolderName(DuplicateMergeCandidateGroupTarget target)
    {
        string keep = (target.KeepFolder ?? target.Folders.FirstOrDefault() ?? string.Empty).TrimEnd('\\', '/');
        string name = System.IO.Path.GetFileName(keep);
        return string.IsNullOrEmpty(name) ? keep : name;
    }

    /// <summary>
    /// 完了したグループの結果を、状態表の現在のKEEPで導いて一括で書き込みます。UIスレッドで呼びます。
    /// 状態表から消えたグループの結果は捨て、どの件数にも数えません。
    /// </summary>
    private (DuplicateMergeVerificationRunResult Result, bool Cancelled) WriteResults(
        Run run, IReadOnlyList<DuplicateMergeCandidateGroupTarget> targets)
    {
        var writes = new List<DuplicateMergeVerificationWrite>();
        foreach ((DuplicateMergeCandidateGroupTarget target, DuplicateMergeGroupInspection inspection) in run?.Completed ?? [])
        {
            if (!list.TryGetState(target.Group, out DuplicateMergeCandidateGroupState state)) { continue; }
            writes.Add(new DuplicateMergeVerificationWrite(target.Group, inspection.Evaluate(state.KeepFolder), inspection));
        }
        IReadOnlyList<DuplicateMergeVerificationWrite> accepted = list.WriteVerifications(writes);
        int completedCount = run?.Completed.Count ?? 0;
        int unverified = targets.Skip(completedCount).Count(target => list.TryGetState(target.Group, out _));
        bool cancelled = unverified > 0;
        return (
            new DuplicateMergeVerificationRunResult(
                cancelled ? DuplicateMergeVerificationRunStatus.Cancelled : DuplicateMergeVerificationRunStatus.Completed,
                accepted.Count,
                accepted.Count(write => write.Result.Stage == DuplicateMergeVerificationStage.Safe),
                accepted.Count(write => write.Result.Stage == DuplicateMergeVerificationStage.Caution),
                accepted.Count(write => write.Result.Stage == DuplicateMergeVerificationStage.Unmergeable),
                unverified),
            cancelled);
    }

    /// <summary>結果の案内です。安全の件数によらず同じ書式で、取消で着手しなかったグループがあれば「未確認」の件数を加えます。</summary>
    private static string BuildResultMessage(DuplicateMergeVerificationRunResult result)
    {
        string format = result.Unverified > 0
            ? Resources.Duplicate_merge_candidate_verify_result_cancelled_format
            : Resources.Duplicate_merge_candidate_verify_result_format;
        return string.Format(format, result.Checked, result.Safe, result.Caution, result.Unmergeable, result.Unverified);
    }

    /// <summary>任意の案内です。表示の失敗は診断へ残すだけで、確認の結果へ影響させません。</summary>
    private async Task ShowOptionalNotificationAsync(UiMessageRequest request, string routeName)
    {
        try
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(request);
            UiDialogRoute.ThrowIfNotShown(result, routeName);
        }
        catch (Exception exception)
        {
            try { NLogWrapper.FileLogger?.Warn(exception, routeName); }
            catch { /* 任意の案内の診断失敗を確認の結果へ合成しません。 */ }
        }
    }
}
